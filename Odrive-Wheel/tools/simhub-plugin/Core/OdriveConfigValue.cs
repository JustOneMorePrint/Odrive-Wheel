using System;
using System.Globalization;

namespace OdriveWheel.SimHubPlugin
{
    public enum ReadbackVerdict
    {
        /// <summary>The reply identifies the value, and it is the one that was written.</summary>
        Verified,
        /// <summary>The reply fits, but cannot prove it: older firmware rounds and cuts its replies.</summary>
        NotVerified,
        /// <summary>The board holds something else.</summary>
        Differs,
    }

    /// <summary>
    /// Values of writable ODrive parameters: what a user may type, what goes on the line and how a reply
    /// compares to it. Always invariant culture, whatever SimHub runs with. A float is what the firmware
    /// stores, a float32, so that is what gets compared.
    /// </summary>
    public static class OdriveConfigValue
    {
        /// <summary>Checks typed text against type and catalog limits. A boolean takes 0 or 1. A float must be
        /// finite as a float32, and the command value is that float32, which can differ from what was typed.</summary>
        public static bool TryParseInput(ParameterDefinition parameter, string text, out string commandValue, out double value, out string error)
        {
            commandValue = null;
            value = 0.0;
            error = null;
            var trimmed = (text ?? "").Trim();
            if (trimmed.Length == 0)
            {
                error = "no value entered";
                return false;
            }

            switch (parameter.Type)
            {
                case ParameterType.Boolean:
                    if (trimmed != "0" && trimmed != "1")
                    {
                        error = "must be 0 or 1";
                        return false;
                    }
                    commandValue = trimmed;
                    value = trimmed == "1" ? 1.0 : 0.0;
                    return true;

                case ParameterType.Float:
                    if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double typed)
                        || double.IsNaN(typed) || double.IsInfinity(typed))
                    {
                        error = "is not a finite number, use a point as decimal separator";
                        return false;
                    }
                    float stored = (float)typed;
                    if (float.IsInfinity(stored) || (stored == 0.0f && typed != 0.0))
                    {
                        error = "does not fit a float32";
                        return false;
                    }
                    if (parameter.Min is double min && typed < min)
                    {
                        error = "must not be below " + min.ToString(CultureInfo.InvariantCulture);
                        return false;
                    }
                    if (parameter.Max is double max && typed > max)
                    {
                        error = "must not be above " + max.ToString(CultureInfo.InvariantCulture);
                        return false;
                    }
                    if (stored == 0.0f) stored = 0.0f;  // no negative zero on the line
                    commandValue = stored.ToString("R", CultureInfo.InvariantCulture);
                    value = stored;
                    return true;

                default:
                    error = "this type cannot be edited";
                    return false;
            }
        }

        /// <summary>A reply as a number, infinity included. NaN or anything else is no usable value.</summary>
        public static bool TryParseReply(ParameterDefinition parameter, string reply, out double value)
        {
            value = 0.0;
            if (reply == null) return false;
            if (parameter.Type == ParameterType.Boolean)
            {
                if (reply != "0" && reply != "1") return false;
                value = reply == "1" ? 1.0 : 0.0;
                return true;
            }
            return TryParseFloatReply(reply, out value);
        }

        /// <summary>
        /// A reply as a value that relations and a write order can rest on. If the firmware of this connection
        /// declared that floats are exact, any float it replies is. Without a declaration a reply older firmware
        /// could have produced does not qualify, however it parses: the value may be rounded or cut, and reading
        /// the same text again settles nothing. A declaration that leaves it out rules out every float.
        /// </summary>
        public static bool TryParseExactReply(ParameterDefinition parameter, string reply, FirmwareCapabilities capabilities, out double value)
        {
            value = 0.0;
            if (reply == null) return false;
            if (parameter.Type == ParameterType.Float && capabilities != null)
                return capabilities.FloatsAreExact && TryParseReply(parameter, reply, out value);

            return !OdriveParameterFormat.Format(parameter, reply, capabilities).MayBeCutOff
                && !MayBeOlderFloatFormat(reply)
                && TryParseReply(parameter, reply, out value);
        }

        /// <summary>The measured bus voltage. Unlike a setting it may be rounded: it moves by far more than the
        /// last digit between two readings anyway, and any reading can look like older firmware.</summary>
        internal static bool TryParseBusVoltage(string reply, out double value)
            => TryParseFloatReply(reply, out value) && !double.IsInfinity(value);

        internal static bool TryParseFloatReply(string reply, out double value)
        {
            value = 0.0;
            switch (reply)
            {
                case "inf":
                    value = double.PositiveInfinity;
                    return true;
                case "-inf":
                    value = double.NegativeInfinity;
                    return true;
            }
            if (!double.TryParse(reply, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                || double.IsNaN(parsed) || double.IsInfinity(parsed))
                return false;
            value = (float)parsed;
            return true;
        }

        /// <summary>Compares what a read returned with what was written, without a declaration of the firmware.</summary>
        public static ReadbackVerdict CompareReadback(ParameterDefinition parameter, string commandValue, string reply, out string detail)
            => CompareReadback(parameter, commandValue, reply, null, out detail);

        /// <summary>Compares what a read returned with what was written. <paramref name="detail"/> says why,
        /// unless it is verified. With floats declared exact the float32 values decide, what the reply looks
        /// like does not matter.</summary>
        public static ReadbackVerdict CompareReadback(ParameterDefinition parameter, string commandValue, string reply, FirmwareCapabilities capabilities, out string detail)
        {
            detail = null;
            if (parameter.Type == ParameterType.Boolean)
            {
                if (reply == commandValue) return ReadbackVerdict.Verified;
                if (reply == "0" || reply == "1")
                {
                    detail = "the board reports " + reply;
                    return ReadbackVerdict.Differs;
                }
                detail = "unexpected reply (" + reply + ")";
                return ReadbackVerdict.NotVerified;
            }

            double expected = (float)double.Parse(commandValue, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (!TryParseFloatReply(reply, out double actual))
            {
                detail = reply == "nan" ? "the board reports nan" : "unexpected reply (" + reply + ")";
                return reply == "nan" ? ReadbackVerdict.Differs : ReadbackVerdict.NotVerified;
            }

            if (capabilities != null && !capabilities.FloatsAreExact)
            {
                detail = "the firmware does not declare exact floats, the reply was " + reply;
                return ReadbackVerdict.NotVerified;
            }

            if (capabilities != null || !MayBeOlderFloatFormat(reply))
            {
                if (actual == expected) return ReadbackVerdict.Verified;
                detail = "the board reports " + reply;
                return ReadbackVerdict.Differs;
            }

            // Older firmware: the value only has to be close enough to explain this reply.
            int point = reply.IndexOf('.');
            if (point < 0)
            {
                detail = "the reply " + reply + " may be cut off";
                return ReadbackVerdict.NotVerified;
            }
            double lastDigit = Math.Pow(10.0, -(reply.Length - point - 1));
            if (Math.Abs(actual - expected) > lastDigit + Math.Abs(expected) * 1e-7)
            {
                detail = "the board reports " + reply;
                return ReadbackVerdict.Differs;
            }
            detail = "the reply " + reply + " may be rounded or cut off by older firmware";
            return ReadbackVerdict.NotVerified;
        }

        /// <summary>
        /// Could this reply come from older firmware? That one wrote %f into 10 bytes: always six decimals,
        /// or fewer if the 9 characters ran out. Newer firmware writes %.9g, which has no trailing zeros, an
        /// exponent where that is shorter, and can be longer than 9 characters. So only replies in the common
        /// range are in doubt, and those count as older, even if newer firmware can produce them too: only a
        /// declaration through sys.capabilities? tells the two apart.
        /// </summary>
        internal static bool MayBeOlderFloatFormat(string reply)
        {
            if (reply.Length > OdriveProtocol.OdriveReadLimit) return false;
            if (reply.IndexOf('e') >= 0 || reply.IndexOf('E') >= 0) return false;
            if (reply == "inf" || reply == "-inf" || reply == "nan") return false;

            int point = reply.IndexOf('.');
            if (point < 0) return reply.Length == OdriveProtocol.OdriveReadLimit;
            int decimals = reply.Length - point - 1;
            return reply.Length == OdriveProtocol.OdriveReadLimit ? decimals <= 6 : decimals == 6;
        }
    }
}
