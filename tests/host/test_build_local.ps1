# Regression tests for build-local.ps1 on Windows. PowerShell, MSYS2 bash, mingw32-make
# and git are real; only the firmware is a stand-in (a small Makefile instead of the ARM
# build), in a throwaway repository whose path has spaces, brackets and an apostrophe.
#
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tests\host\test_build_local.ps1
# Exit code: 0 passed, 1 failed, 2 not run because a tool is missing.

param(
  [string]$MsysBash = "C:\msys64\usr\bin\bash.exe"
)

# Native tools report on stderr, and every result below is checked explicitly.
$ErrorActionPreference = "Continue"

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = (Resolve-Path -LiteralPath (Join-Path $here "..\..")).Path

function Stop-NotRun {
  param([string]$Reason)
  Write-Output "NOT RUN: $Reason"
  exit 2
}

$mingwMake = Join-Path (Split-Path -Parent $MsysBash) "..\..\mingw64\bin\mingw32-make.exe"
if (!(Test-Path -LiteralPath $MsysBash)) { Stop-NotRun "MSYS2 bash not found at $MsysBash" }
if (!(Test-Path -LiteralPath $mingwMake)) { Stop-NotRun "mingw32-make not found at $mingwMake" }
if (!(Get-Command git -ErrorAction SilentlyContinue)) { Stop-NotRun "git not on PATH" }

# build-local.ps1 installs missing Python packages, which a test must never trigger.
$venvScripts = Join-Path $root ".venv\Scripts"
if (Test-Path -LiteralPath (Join-Path $venvScripts "python.exe")) {
  $env:PATH = "$venvScripts;$env:PATH"
}
& python -c "import yaml, jinja2, jsonschema" 2>$null
if ($LASTEXITCODE -ne 0) { Stop-NotRun "Python with pyyaml, jinja2 and jsonschema not found" }

$script:checks = 0
$script:failures = 0

function Test-That {
  param([string]$Name, [bool]$Condition, [string]$Detail = "")
  $script:checks++
  if (!$Condition) {
    $script:failures++
    Write-Output "FAILED: $Name"
    if ($Detail) { Write-Output "  $($Detail.Trim() -replace "`n", "`n  ")" }
  }
}

function Test-Equal {
  param([string]$Name, [string]$Expected, [string]$Actual)
  Test-That $Name ($Expected -ceq $Actual) "expected: $Expected`nactual:   $Actual"
}

$tag = [Guid]::NewGuid().ToString("N").Substring(0, 8)
$base = Join-Path ([IO.Path]::GetTempPath()) "odw test $tag (x86) [y] it's"
$toolchain = Join-Path $base "tool chain (x86) [arm] it's"
[void][IO.Directory]::CreateDirectory($toolchain)
[IO.File]::WriteAllText((Join-Path $toolchain "toolchain-probe"), "#!/bin/sh`n")

$fakeMakefile = @'
all:
	@test -z "$$FAKE_MAKE_FAIL"
	@command -v toolchain-probe > /dev/null
	@mkdir -p build
	@sh tools/gen_build_info.sh build/build_info.h > /dev/null
	@printf '%s' "$$PROBE_KEEP" > build/probe_keep.txt
	@printf '%s' "$$PROBE_OTHER" > build/probe_other.txt
	@printf '%s' "$$MSYS2_ENV_CONV_EXCL" > build/conv_excl.txt
	@echo line-on-stderr 1>&2
	@echo stand-in > build/odrive-wheel.bin

clean:
	rm -rf build
'@ -replace "`r", ""

$fakeGenerator = @'
import sys
open(sys.argv[sys.argv.index("--output") + 1], "w").close()
'@ -replace "`r", ""

function New-FakeRepo {
  param([string]$Name)
  $repo = Join-Path $base $Name
  $odrive = Join-Path $repo "ODrive-fw-v0.5.6\ODrive-fw-v0.5.6"
  foreach ($dir in "Odrive-Wheel\tools", "ODrive-fw-v0.5.6\ODrive-fw-v0.5.6\Firmware",
                   "ODrive-fw-v0.5.6\ODrive-fw-v0.5.6\tools\odrive",
                   "ODrive-fw-v0.5.6\ODrive-fw-v0.5.6\tools\fibre-tools") {
    [void][IO.Directory]::CreateDirectory((Join-Path $repo $dir))
  }
  [IO.File]::Copy((Join-Path $root "build-local.ps1"), (Join-Path $repo "build-local.ps1"))
  [IO.File]::Copy((Join-Path $root "Odrive-Wheel\tools\gen_build_info.sh"),
                  (Join-Path $repo "Odrive-Wheel\tools\gen_build_info.sh"))
  [IO.File]::WriteAllText((Join-Path $repo "Odrive-Wheel\Makefile"), $fakeMakefile)
  [IO.File]::WriteAllText((Join-Path $repo ".gitignore"), "build/`nautogen/`n")
  [IO.File]::WriteAllText((Join-Path $odrive "tools\odrive\version.py"), $fakeGenerator)
  [IO.File]::WriteAllText((Join-Path $odrive "tools\fibre-tools\interface_generator.py"), "")
  [IO.File]::WriteAllText((Join-Path $odrive "Firmware\interface_generator_stub.py"), $fakeGenerator)
  & git -C $repo init -q
  & git -C $repo config user.name test
  & git -C $repo config user.email test@example.invalid
  & git -C $repo config commit.gpgsign false
  & git -C $repo config core.autocrlf false
  & git -C $repo add -A
  & git -C $repo commit -q -m init
  return $repo
}

function Set-TestEnv {
  param([hashtable]$Values)
  $old = @{}
  foreach ($name in $Values.Keys) {
    $old[$name] = [Environment]::GetEnvironmentVariable($name)
    [Environment]::SetEnvironmentVariable($name, $Values[$name])
  }
  return $old
}

# Runs the script as its own process, the way a CI step or a terminal call would.
function Invoke-BuildLocal {
  param([string]$Repo, [string[]]$Options = @(), [hashtable]$TestEnv = @{},
        [string]$Bash = $MsysBash)
  $old = Set-TestEnv $TestEnv
  $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Repo "build-local.ps1") `
    -ToolchainBin $toolchain -SkipSubmoduleInit -MsysBash $Bash @Options 2>&1 | Out-String
  $code = $LASTEXITCODE
  [void](Set-TestEnv $old)
  return @{ Code = $code; Output = $output }
}

# Runs the script inside a PowerShell session and returns what the caller sees there.
function Invoke-BuildLocalInSession {
  param([string]$Repo, [string[]]$Options = @(), [hashtable]$TestEnv = @{},
        [string]$Bash = $MsysBash, [string]$Redirect = "")
  $quote = { param($s) "'" + ($s -replace "'", "''") + "'" }
  $call = "& $(& $quote (Join-Path $Repo 'build-local.ps1')) -ToolchainBin $(& $quote $toolchain) " +
          "-SkipSubmoduleInit -MsysBash $(& $quote $Bash) $($Options -join ' ') $Redirect"
  # The catch keeps the session alive, so only $LASTEXITCODE can report the failure.
  $command = "`$global:LASTEXITCODE = 0; try { $call } catch { Write-Output `$_ }; exit `$LASTEXITCODE"
  $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
  $old = Set-TestEnv $TestEnv
  $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -EncodedCommand $encoded 2>&1 | Out-String
  $code = $LASTEXITCODE
  [void](Set-TestEnv $old)
  return @{ Code = $code; Output = $output }
}

function Get-Artifact {
  param([string]$Repo, [string]$Name)
  return Join-Path $Repo "Odrive-Wheel\build\$Name"
}

function Get-BuildId {
  param([string]$Repo)
  $path = Get-Artifact $Repo "build_info.h"
  if (![IO.File]::Exists($path)) { return "<no header>" }
  $match = [regex]::Match([IO.File]::ReadAllText($path), '#define BUILD_ID "((?:\\[0-7]{3})*)"')
  if (!$match.Success) { return "<no BUILD_ID>" }
  $bytes = [byte[]]([regex]::Matches($match.Groups[1].Value, '[0-7]{3}') | ForEach-Object { [Convert]::ToByte($_.Value, 8) })
  return [Text.Encoding]::UTF8.GetString($bytes)
}

function Get-Hash {
  param([string]$Repo)
  return (& git -C $Repo rev-parse --short=12 HEAD)
}

function New-StaleArtifacts {
  param([string]$Repo)
  [void][IO.Directory]::CreateDirectory((Get-Artifact $Repo ""))
  [IO.File]::WriteAllText((Get-Artifact $Repo "odrive-wheel.bin"), "stale")
  [IO.File]::WriteAllText((Get-Artifact $Repo "build-local.done"), "stale")
}

try {
  $repo = New-FakeRepo "repo (x86) [y] it's"

  # The whole shell command must arrive: PowerShell 5.1 split it at double quotes.
  $run = Invoke-BuildLocal $repo
  Test-Equal "clean build succeeds in a hostile path" "0" $run.Code
  Test-That "clean build reports success" ($run.Output -match "Build OK") $run.Output
  Test-That "clean build created the binary" ([IO.File]::Exists((Get-Artifact $repo "odrive-wheel.bin"))) $run.Output
  Test-That "completion marker is removed" (![IO.File]::Exists((Get-Artifact $repo "build-local.done")))
  Test-Equal "build id comes from git through the appended PATH" "$(Get-Hash $repo) branch=$(& git -C $repo symbolic-ref --short HEAD)" (Get-BuildId $repo)

  # -NoClean keeps what a clean build removes.
  $keep = Get-Artifact $repo "keep.txt"
  [IO.File]::WriteAllText($keep, "keep")
  $run = Invoke-BuildLocal $repo @("-NoClean")
  Test-Equal "-NoClean succeeds" "0" $run.Code
  Test-That "-NoClean keeps existing files" ([IO.File]::Exists($keep)) $run.Output
  $run = Invoke-BuildLocal $repo
  Test-Equal "second clean build succeeds" "0" $run.Code
  Test-That "clean build removes existing files" (![IO.File]::Exists($keep)) $run.Output

  # Compiler warnings on stderr must not abort a build whose caller redirects stderr.
  $run = Invoke-BuildLocalInSession $repo -Redirect "2>&1 6>&1 | Out-Null"
  Test-Equal "stderr output with redirected error stream is no failure" "0" $run.Code

  # Metadata across PowerShell, MSYS bash, native make, MSYS sh.
  foreach ($branch in "x=/y", "x=/usr/bin", "=/x", "#/x", "it's", "`$(touch`${IFS}PWNED)", "``touch`${IFS}PWNED``", "say`"hi`"", ("umlaut-" + [char]0xE4)) {
    $run = Invoke-BuildLocal $repo -TestEnv @{ BUILD_GIT_BRANCH = $branch }
    Test-Equal "env branch builds: $branch" "0" $run.Code
    Test-Equal "env branch stays intact: $branch" "$(Get-Hash $repo) branch=$branch" (Get-BuildId $repo)
  }
  foreach ($branch in "x=/z", "it's", "`$(touch`${IFS}PWNED)") {
    & git -C $repo checkout -q -b $branch
    $run = Invoke-BuildLocal $repo
    Test-Equal "git branch builds: $branch" "0" $run.Code
    Test-Equal "git branch stays intact: $branch" "$(Get-Hash $repo) branch=$branch" (Get-BuildId $repo)
  }
  Test-That "no branch name was executed" (@(Get-ChildItem -LiteralPath $repo -Recurse -Force -Filter "PWNED*" -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '\\\.git\\' }).Count -eq 0)

  # Existing exclusions stay in force, and conversion stays on for everything else.
  $run = Invoke-BuildLocal $repo -TestEnv @{
    MSYS2_ENV_CONV_EXCL = "PROBE_KEEP"; PROBE_KEEP = "x=/y"; PROBE_OTHER = "x=/y"; BUILD_GIT_BRANCH = "x=/y"
  }
  Test-Equal "build with an existing exclusion succeeds" "0" $run.Code
  Test-Equal "existing entry is kept" "PROBE_KEEP;BUILD_GIT_BRANCH" ([IO.File]::ReadAllText((Get-Artifact $repo "conv_excl.txt")))
  Test-Equal "existing entry still takes effect" "x=/y" ([IO.File]::ReadAllText((Get-Artifact $repo "probe_keep.txt")))
  Test-Equal "branch is exempt next to it" "$(Get-Hash $repo) branch=x=/y" (Get-BuildId $repo)
  $other = [IO.File]::ReadAllText((Get-Artifact $repo "probe_other.txt"))
  Test-That "other variables are still converted" ($other -cne "x=/y") "actual: $other"

  # Make failure, with artifacts of an earlier build present.
  New-StaleArtifacts $repo
  $run = Invoke-BuildLocal $repo @("-NoClean") @{ FAKE_MAKE_FAIL = "1" }
  Test-That "make failure gives an error code" ($run.Code -ne 0) $run.Output
  Test-That "make failure does not report success" ($run.Output -notmatch "Build OK") $run.Output
  $run = Invoke-BuildLocalInSession $repo @("-NoClean") @{ FAKE_MAKE_FAIL = "1" }
  Test-That "make failure gives an error code in a session" ($run.Code -ne 0) $run.Output

  # A shell that returns 0 without doing the work: what the split command looked like.
  New-StaleArtifacts $repo
  $fakeBash = Join-Path $base "shell returning zero (x86).cmd"
  [IO.File]::WriteAllText($fakeBash, "@exit /b 0`r`n")
  $run = Invoke-BuildLocal $repo @("-NoClean") -Bash $fakeBash
  Test-That "shell that did no work gives an error code" ($run.Code -ne 0) $run.Output
  Test-That "stale artifacts do not report success" ($run.Output -notmatch "Build OK") $run.Output

  # Shell call failure.
  $missing = Join-Path $base "no such shell.exe"
  $run = Invoke-BuildLocal $repo -Bash $missing
  Test-That "missing shell gives an error code" ($run.Code -ne 0) $run.Output
  $run = Invoke-BuildLocalInSession $repo -Bash $missing
  Test-That "missing shell gives an error code in a session" ($run.Code -ne 0) $run.Output

  # Directory change failure in the real bash: the firmware path exists but is a file.
  $broken = New-FakeRepo "broken (x86)"
  $firmwareDir = Join-Path $broken "Odrive-Wheel"
  [IO.Directory]::Delete($firmwareDir, $true)
  [IO.File]::WriteAllText($firmwareDir, "not a directory")
  $run = Invoke-BuildLocal $broken
  Test-That "failed directory change gives an error code" ($run.Code -ne 0) $run.Output
  Test-That "failed directory change stops in cd" ($run.Output -match "cd:") $run.Output
  Test-That "failed directory change does not report success" ($run.Output -notmatch "Build OK") $run.Output
}
finally {
  Remove-Item -LiteralPath $base -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output "test_build_local.ps1: $script:checks checks, $script:failures failed"
if ($script:failures -ne 0) { exit 1 }
exit 0
