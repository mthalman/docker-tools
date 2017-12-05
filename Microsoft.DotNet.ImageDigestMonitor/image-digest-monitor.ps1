param(
    [string]$Subscriptions = 'subscriptions.json',
    [string]$ImageBuilderImageName = 'microsoft/dotnet-buildtools-prereqs:image-builder-jessie-20171031123612',
    [string]$VersionsGitUserName,
    [string]$VersionsGitEmail,
    [string]$VersionsGitPassword,
    [string]$VersionsGitBranch = 'master',
    [string]$VersionsGitOwner,
    [string]$Architecture
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

$repoSandbox = "./RepoSandbox";
New-Item -ItemType Directory -Path $repoSandbox
Try {
    Push-Location $repoSandbox
    Try {
        $subscriptionsJson.Repos.PSObject.Properties |
            ForEach-Object {
            $repoUrl = $_.Name
            exec git clone $repoUrl
            # $_.Value.Branches | ForEach-Object {
            #     Write-Host "Cloning $repoUrl $_"
            #     exec git clone $repoUrl $_
            # }
        }
    }
    Finally {
        Pop-Location
    }
}
Finally {
    Remove-Item -Force -Recurse $repoSandbox
}

# & docker run --rm `
#     -v /var/run/docker.sock:/var/run/docker.sock `
#     -v "${repoRoot}:/repo" `
#     -w /repo `
#     $ImageBuilderImageName `
#     generateTagsReadme "https://github.com/dotnet/${RepoName}/blob/${Branch}"