param(
    [string]$Subscriptions = "${PSScriptRoot}/subscriptions.json",
    [string]$ImageBuilderImageName = 'microsoft/dotnet-buildtools-prereqs:image-builder-jessie-20171031123612',
    [string]$VersionsGitUserName = 'dotnet-build-bot',
    [string]$VersionsGitEmail = 'dotnet-build-bot@microsoft.com',
    [string]$VersionsGitAccessToken,
    [string]$VersionsGitBranch = 'master',
    [string]$VersionsGitOwner = 'dotnet',
    [string]$Architecture = 'amd64'
)

$ErrorActionPreference = 'Stop'

function exec($cmd) {
    Write-Host -ForegroundColor Cyan ">>> $cmd $args"

    $originalErrorPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & $cmd @args
    $exitCode = $LastExitCode
    $ErrorActionPreference = $originalErrorPreference

    if ($exitCode -ne 0) {
        Write-Host -ForegroundColor Red "<<< [$exitCode] $cmd $args"
        fatal 'Command exited with non-zero code'
    }
}

exec docker pull $ImageBuilderImageName

$subscriptionsJson = Get-Content $Subscriptions | ConvertFrom-Json

[string]$RepoSandbox = "$(Get-Location)/RepoSandbox"
New-Item -ItemType Directory -Path $repoSandbox

Try {
    Push-Location $repoSandbox
    Try {
        $subscriptionsJson.Repos.PSObject.Properties |
            ForEach-Object {
                $repoName = $_.Name
                exec git clone $_.Value.Url $repoName

                Push-Location $repoName
                try {
                    $_.Value.Branches | ForEach-Object {
                        $branch = $_
                        exec git checkout $branch

                        exec docker run --rm `
                            -v /var/run/docker.sock:/var/run/docker.sock `
                            -v "${repoSandbox}/${repoName}:/repo" `
                            -w /repo `
                            $ImageBuilderImageName `
                            updateVersions `
                                $VersionsGitUserName `
                                $VersionsGitEmail `
                                $VersionsGitAccessToken `
                                --git-branch $VersionsGitBranch `
                                --git-owner $VersionsGitOwner `
                                --architecture $Architecture
                    }
                }
                Finally {
                    Pop-Location
                }
            }
    }
    Finally {
        Pop-Location
    }
}
Finally {
    Remove-Item -Force -Recurse $repoSandbox
}
