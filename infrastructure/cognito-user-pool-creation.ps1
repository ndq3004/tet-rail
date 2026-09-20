# Step 1: Create User Pool
$userPool = aws cognito-idp create-user-pool `
    --profile quan `
    --pool-name MyUserPool `
    --policies '{\"PasswordPolicy\":{\"MinimumLength\":8,\"RequireUppercase\":true,\"RequireLowercase\":true,\"RequireNumbers\":true,\"RequireSymbols\":true}}' `
    --auto-verified-attributes email | ConvertFrom-Json

$USERPOOL_ID = $userPool.UserPool.Id; #'us-east-1_2wgGo7zvy';
Write-Host "User Pool ID: $USERPOOL_ID"

# Step 2: Create User Pool Client
$client = aws cognito-idp create-user-pool-client `
    --profile quan `
    --user-pool-id $USERPOOL_ID `
    --client-name MyAppClient `
    --no-generate-secret `
    --supported-identity-providers "COGNITO" `
    --allowed-o-auth-flows-user-pool-client `
    --allowed-o-auth-flows code `
    --allowed-o-auth-scopes openid email profile `
    --callback-urls "http://localhost:5173/callback" `
    --logout-urls "http://localhost:5173/logout" | ConvertFrom-Json

$CLIENT_ID = $client.UserPoolClient.ClientId #5j71ltlnerr7875fa9ho7jshal
Write-Host "Client ID: $CLIENT_ID"

# Step 3: Create Cognito Domain
$domain = "tetrail-$($USERPOOL_ID.Split('_')[1].ToLowerInvariant())"
aws cognito-idp create-user-pool-domain `
    --profile quan `
    --domain $domain `
    --user-pool-id $USERPOOL_ID

# Step 4: Print Hosted UI Login URL
Write-Host "`n=== Hosted UI Login URL ==="
Write-Host "https://$domain.auth.us-east-1.amazoncognito.com/login?client_id=$CLIENT_ID&response_type=code&scope=email+openid+profile&redirect_uri=http://localhost:5173/callback"
