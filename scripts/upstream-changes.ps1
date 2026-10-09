<#
.SYNOPSIS
  Lists the Mac Signet commits not yet ported to Windows.

.DESCRIPTION
  The `upstream` submodule is pinned to the last Mac commit that has been ported. This fetches
  upstream and prints every newer commit with the files it touches, so each can be ported in
  order. After porting, advance the pin with:

    git -C upstream checkout <commit>
    git add upstream
    git commit -m "Port upstream <short sha>: <subject>"

.PARAMETER Ref
  The upstream ref to compare against (default origin/main).

.PARAMETER NoFetch
  Skip `git fetch`, e.g. when offline.
#>
param(
    [string]$Ref = "origin/main",
    [switch]$NoFetch
)

$ErrorActionPreference = "Stop"
$upstream = Join-Path $PSScriptRoot "..\upstream"

if (-not $NoFetch) { git -C $upstream fetch --quiet origin }

$pinned = git -C $upstream rev-parse --short HEAD
$commits = @(git -C $upstream rev-list --reverse "HEAD..$Ref")

if ($commits.Count -eq 0) {
    Write-Output "Up to date: upstream pinned at $pinned, nothing new on $Ref."
    exit 0
}

Write-Output "Pinned at $pinned; $($commits.Count) commit(s) on $Ref to port (oldest first):"
foreach ($c in $commits) {
    Write-Output ""
    git -C $upstream log -1 --format="%h %ad %s" --date=short $c
    git -C $upstream diff-tree --no-commit-id --name-status -r $c | ForEach-Object { "    $_" }
}
