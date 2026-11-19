<#
.SYNOPSIS
    Builds and deploys zanak-bot to Azure Container Instances (ACI) using Azure Container Registry (ACR).

.EXAMPLE
    .\tools\lichess-bot\deploy-aci.ps1 `
        -ResourceGroup "zanak-bot-rg" `
        -Location "westeurope" `
        -AcrName "zanakbotacr123" `
        -LichessToken $env:LICHESS_BOT_TOKEN
#>
[CmdletBinding()]
param(
    [string]$Subscription = "",
    [string]$ResourceGroup = "zanak-bot-rg",
    [string]$Location = "westeurope",
    [Parameter(Mandatory = $true)]
    [string]$AcrName,
    [string]$ContainerName = "zanak-lichess-bot",
    [string]$ImageTag = "zanak-lichess-bot:latest",
    [double]$Cpu = 2,
    [double]$MemoryGb = 1.5,
    [switch]$LocalBuild,
    [Parameter(Mandatory = $true)]
    [string]$LichessToken
)

$ErrorActionPreference = "Stop"

$SubArgs = @()
if (-not [string]::IsNullOrWhiteSpace($Subscription)) {
    $SubArgs = @("--subscription", $Subscription)
}

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
Write-Host "Repo root: $RepoRoot"

Write-Host "1/4 Ensuring resource group '$ResourceGroup' exists in '$Location'..."
az group create @SubArgs --name $ResourceGroup --location $Location --only-show-errors --output none

Write-Host "2/4 Ensuring Azure Container Registry '$AcrName' exists..."
az acr create @SubArgs --resource-group $ResourceGroup --name $AcrName --sku Basic --admin-enabled true --only-show-errors --output none

$AcrServer = az acr show @SubArgs --name $AcrName --query loginServer -o tsv --only-show-errors
$AcrUser = az acr credential show @SubArgs --name $AcrName --query username -o tsv --only-show-errors
$AcrPassword = az acr credential show @SubArgs --name $AcrName --query "passwords[0].value" -o tsv --only-show-errors

if ($LocalBuild) {
    Write-Host "3/4 Building and pushing '$AcrServer/$ImageTag' locally via Docker..."
    docker build -f (Join-Path $RepoRoot "tools\lichess-bot\Dockerfile") -t "$AcrServer/$ImageTag" $RepoRoot
    if ($LASTEXITCODE -ne 0) { throw "docker build failed." }

    $AcrPassword | docker login $AcrServer --username $AcrUser --password-stdin
    if ($LASTEXITCODE -ne 0) { throw "docker login failed." }

    docker push "$AcrServer/$ImageTag"
    if ($LASTEXITCODE -ne 0) { throw "docker push failed." }
}
else {
    Write-Host "3/4 Building container image '$ImageTag' in ACR (no local Docker daemon required)..."
    az acr build @SubArgs `
        --registry $AcrName `
        --image $ImageTag `
        --file (Join-Path $RepoRoot "tools\lichess-bot\Dockerfile") `
        --only-show-errors `
        $RepoRoot
}

Write-Host "4/4 Deploying Azure Container Instance '$ContainerName'..."
az container create @SubArgs `
    --resource-group $ResourceGroup `
    --name $ContainerName `
    --image "$AcrServer/$ImageTag" `
    --os-type Linux `
    --cpu $Cpu `
    --memory $MemoryGb `
    --restart-policy Always `
    --registry-login-server $AcrServer `
    --registry-username $AcrUser `
    --registry-password $AcrPassword `
    --secure-environment-variables LICHESS_BOT_TOKEN=$LichessToken `
    --only-show-errors `
    --output table

Write-Host ""
Write-Host "Deployment complete! Stream logs with:"
Write-Host "  az container logs --resource-group $ResourceGroup --name $ContainerName --follow"
