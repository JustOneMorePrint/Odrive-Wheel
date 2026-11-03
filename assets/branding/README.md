# ADDD branding

**ADDD** stands for **Another DIY Direct Drive (Wheel)**.

This directory is the shared location for the supplied ADDD logo assets. Add the original files here without
modifying or removing the existing logos elsewhere in the repository.

Only two original PNG files from `ADDD_Icon_Set_Sauber.zip` are included:

| Asset | Size | Use |
|---|---|---|
| [addd-blue-512.png](addd-blue-512.png) | 512 × 512, transparent | Default for README files and documentation; displayed smaller for sharp rendering on high-DPI screens |
| [addd-white-256.png](addd-white-256.png) | 256 × 256, transparent | SimHub interface; Claude integrates it into the application resources |

The original ZIP paths are `Blau/ADDD_blau_512x512.png` and `Weiss_SimHub/ADDD_weiss_256x256.png`.
Their image contents are unchanged. Other resolutions, Windows ICO files and the ZIP itself are not included.

Keep asset references relative to the document or project that uses them. Preserve the supplied artwork rather than
recreating it. Application resource references, package/protocol names and removal of old code assets belong to the
separate application integration task.

The existing `Odrive-Wheel/tools/simhub-plugin/Assets/logo.png` and its code references remain in place until that task.

The root README describes the complete product workflow. Implementation and hardware-validation status remain in the
[roadmap](../../docs/ROADMAP.md) and [test record](../../docs/TESTING.md); branding does not establish a feature or compatibility claim.
