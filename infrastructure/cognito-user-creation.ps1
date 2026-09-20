param(
    [Parameter(Mandatory = $true)]
    [string]$UserPoolId,

    [Parameter(Mandatory = $true)]
    [string]$Email,

    [Parameter(Mandatory = $true)]
    [string]$Password,

    [string]$Profile = "quan"
)

# Creates a confirmed user for local development. Avoid putting real passwords in shell history or source control.
aws cognito-idp admin-create-user `
    --profile $Profile `
    --user-pool-id $UserPoolId `
    --username $Email `
    --user-attributes Name=email,Value=$Email Name=email_verified,Value=true `
    --message-action SUPPRESS | Out-Null

aws cognito-idp admin-set-user-password `
    --profile $Profile `
    --user-pool-id $UserPoolId `
    --username $Email `
    --password $Password `
    --permanent | Out-Null

Write-Host "Created Cognito user: $Email"
