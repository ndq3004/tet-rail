User Pool Id: us-east-1_2wgGo7zvy
ClientId: 5j71ltlnerr7875fa9ho7jshal
HOsted UI Login Url: https://tetrail-2wggo7zvy.auth.us-east-1.amazoncognito.com/login?client_id=5j71ltlnerr7875fa9ho7jshal&response_type=code&scope=email+openid+profile&redirect_uri=http://localhost:5173/callback
Callback Url: http://localhost:5173/callback
Logout url: http://localhost:5173/logout
Authentication flow: authentication code flow


Invoke-RestMethod `
  -Method Post `
  -Uri "https://tetrail-2wggo7zvy.auth.us-east-1.amazoncognito.com/oauth2/token" `
  -ContentType "application/x-www-form-urlencoded" `
  -Body @{
    grant_type    = "authorization_code"
    client_id     = "5j71ltlnerr7875fa9ho7jshal"
    code          = "8c695433-faf2-4d34-bbce-b0bf102d32ea"
    redirect_uri  = "http://localhost:5173/callback"
    code_verifier = "<original-PKCE-verifier>"
  }

# Test token
$headers = @{ Authorization = "Bearer <ID_TOKEN>" }
Invoke-RestMethod http://localhost:52757/api/identity/me -Headers $headers
Invoke-RestMethod http://localhost:52757/api/passengers -Headers $headers