# Run against a LOCAL copy of the API only: IIS Express on port 5391 serving
# the build with connectionStrings.config pointed at (localdb)\SFTest.
# The setup step DELETES users/invites/responses - never point it at a real DB.
# End-to-end account-scoping test against the local IIS Express copy
# (LocalDB SFTest). Throwaway data only.
$base = 'http://localhost:5391'
$cs = 'Server=(localdb)\SFTest;Database=SurveyFlow;Integrated Security=true'
$pw = 'Test-Pass-1234'
$script:fails = 0

function Sql($q) {
    $cn = New-Object System.Data.SqlClient.SqlConnection $cs; $cn.Open()
    $c = $cn.CreateCommand(); $c.CommandText = $q; $r = $c.ExecuteScalar(); $cn.Close(); return $r
}
function Call($session, $method, $path, $body = $null, $headers = @{}) {
    $args = @{ Uri = "$base$path"; Method = $method; SkipHttpErrorCheck = $true; Headers = $headers; ContentType = 'application/json' }
    if ($session) { $session.Headers.Clear(); $args.WebSession = $session }
    if ($null -ne $body) { $args.Body = (ConvertTo-Json -InputObject $body -Depth 10 -Compress) }
    $r = Invoke-WebRequest @args
    $json = $null; try { $json = $r.Content | ConvertFrom-Json } catch {}
    return [pscustomobject]@{ Status = [int]$r.StatusCode; Json = $json; Raw = $r.Content }
}
function Check($name, $cond, $detail = '') {
    if ($cond) { "  PASS  $name" } else { "  FAIL  $name  $detail"; $script:fails++ }
}
function Login($email) {
    $s = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $r = Call $s POST '/auth/login' @{ email = $email; password = $pw }
    if ($r.Status -ne 200) { throw "login $email failed: $($r.Status) $($r.Raw)" }
    return $s
}

# --- setup ---------------------------------------------------------------
Sql "DELETE FROM SurveyResponses; DELETE FROM SurveyInvites; DELETE FROM EmailBlacklist; DELETE FROM EmailSuppression; DELETE FROM AiUsage; DELETE FROM Users; DELETE FROM Surveys WHERE created_by LIKE '%@test.local'" | Out-Null
$r = Call $null POST '/auth/seed-admin' @{ email = 'sys@test.local'; password = $pw; full_name = 'Sys Admin' }
"setup: seed-admin -> $($r.Status)"
$acctA = Sql "SELECT id FROM Accounts WHERE slug='periscopeiq'"
$acctB = Sql "IF NOT EXISTS (SELECT 1 FROM Accounts WHERE slug='client-b') INSERT Accounts(id,name,slug) VALUES (LOWER(CONVERT(CHAR(36),NEWID())),'Client B','client-b'); SELECT id FROM Accounts WHERE slug='client-b'"
Sql "INSERT Users(id,email,password_hash,full_name,role,is_active,account_id,is_system_admin)
     SELECT LOWER(CONVERT(CHAR(36),NEWID())),'badmin@test.local',password_hash,'B Admin','admin',1,'$acctB',0 FROM Users WHERE email='sys@test.local';
     INSERT Users(id,email,password_hash,full_name,role,is_active,account_id,is_system_admin)
     SELECT LOWER(CONVERT(CHAR(36),NEWID())),'buser@test.local',password_hash,'B User','user',1,'$acctB',0 FROM Users WHERE email='sys@test.local'" | Out-Null

$sys = Login 'sys@test.local'; $bAdmin = Login 'badmin@test.local'; $bUser = Login 'buser@test.local'

# --- auth/me -------------------------------------------------------------
"auth/me"
$me = (Call $bAdmin GET '/auth/me').Json
Check 'me returns account_id + name' ($me.account_id -eq $acctB -and $me.account_name -eq 'Client B')
Check 'me is_system_admin false for B' ($me.is_system_admin -eq $false)

# --- surveys ---------------------------------------------------------------
"surveys"
$sA = (Call $sys POST '/Survey' @{ title = 'A survey'; status = 'active' }).Json
$sB = (Call $bAdmin POST '/Survey' @{ title = 'B survey'; status = 'active'; account_id = $acctA }).Json
Check 'create stamps caller account (ignores payload account_id)' ($sB.account_id -eq $acctB) "got $($sB.account_id)"
$listB = (Call $bAdmin GET '/Survey').Json
Check 'B lists only B surveys' (@($listB).Count -ge 1 -and -not (@($listB) | Where-Object account_id -ne $acctB))
$listA = (Call $sys GET '/Survey').Json
Check 'A does not see B survey' (-not (@($listA) | Where-Object id -eq $sB.id))
Check 'B cannot update A survey (404)' ((Call $bAdmin PUT "/Survey/$($sA.id)" @{ title = 'hacked' }).Status -eq 404)
Check 'B cannot delete A survey (404)' ((Call $bAdmin DELETE "/Survey/$($sA.id)").Status -eq 404)
Check 'A survey title unchanged' ((Sql "SELECT title FROM Surveys WHERE id='$($sA.id)'") -eq 'A survey')
Check 'anonymous can load survey by id (TakeSurvey)' ((Call $null GET "/Survey/$($sB.id)").Status -eq 200)
Check 'anonymous cannot list surveys (401)' ((Call $null GET '/Survey').Status -eq 401)

# --- system admin account switch ------------------------------------------
"account switching"
$asB = (Call $sys GET '/Survey' $null @{ 'X-Account-Id' = $acctB }).Json
Check 'system admin + X-Account-Id sees B' ((@($asB) | Where-Object id -eq $sB.id) -ne $null)
$bAsA = (Call $bAdmin GET '/Survey' $null @{ 'X-Account-Id' = $acctA }).Json
Check 'non-system admin header ignored' (-not (@($bAsA) | Where-Object account_id -eq $acctA))
Check 'unknown account header -> 400' ((Call $sys GET '/Survey' $null @{ 'X-Account-Id' = 'nope' }).Status -eq 400)

# --- invites -----------------------------------------------------------------
"invites"
$inv = (Call $bAdmin POST '/SurveyInvite' @{ survey_id = $sB.id; email = 'resp@x.com' }).Json
Check 'invite gets server token + password' ($inv.token.Length -ge 32 -and $inv.password.Length -eq 6)
Check 'invite account = B' ($inv.account_id -eq $acctB)
Check 'B cannot invite to A survey (404)' ((Call $bAdmin POST '/SurveyInvite' @{ survey_id = $sA.id; email = 'z@x.com' }).Status -eq 404)
Check 'B cannot bulk-invite to A survey (404)' ((Call $bAdmin POST '/SurveyInvite/bulk' @(@{ survey_id = $sA.id; email = 'z@x.com' })).Status -eq 404)
Check 'anonymous list without token -> 401' ((Call $null GET '/SurveyInvite').Status -eq 401)
$f = [uri]::EscapeDataString((@{ survey_id = $sB.id } | ConvertTo-Json -Compress))
Check 'anonymous filter by survey_id -> 401' ((Call $null GET "/SurveyInvite?filter=$f").Status -eq 401)
$f = [uri]::EscapeDataString((@{ token = $inv.token; status = 'nomatch' } | ConvertTo-Json -Compress))
$byTok = (Call $null GET "/SurveyInvite?filter=$f").Json
Check 'anonymous token lookup returns the invite (extra keys ignored)' (@($byTok).Count -eq 1 -and $byTok[0].id -eq $inv.id)
Check 'A cannot see B invites' (@((Call $sys GET '/SurveyInvite').Json | Where-Object id -eq $inv.id).Count -eq 0)
$r = Call $null PUT "/SurveyInvite/$($inv.id)" @{ status = 'opened'; opened_at = (Get-Date).ToUniversalTime().ToString('o'); email = 'evil@x.com'; survey_id = $sA.id }
Check 'respondent can mark opened' ($r.Status -eq 200)
Check 'respondent cannot change email/survey' ((Sql "SELECT email FROM SurveyInvites WHERE id='$($inv.id)'") -eq 'resp@x.com' -and (Sql "SELECT survey_id FROM SurveyInvites WHERE id='$($inv.id)'") -eq $sB.id)
Check 'respondent cannot set status=sent (400)' ((Call $null PUT "/SurveyInvite/$($inv.id)" @{ status = 'sent' }).Status -eq 400)
Check 'respondent response hides invite fields' ($null -eq $r.Json.password)
Check 'owner full update works' ((Call $bAdmin PUT "/SurveyInvite/$($inv.id)" @{ first_name = 'Pat' }).Json.first_name -eq 'Pat')
Check 'A cannot delete B invite (404)' ((Call $sys DELETE "/SurveyInvite/$($inv.id)").Status -eq 404)

# --- responses -------------------------------------------------------------
"responses"
$resp = (Call $null POST '/SurveyResponse' @{ survey_id = $sB.id; invite_id = $inv.id; answers = @{ q1 = 'a' }; status = 'in_progress'; sentiment_score = 9 }).Json
Check 'anonymous response lands in survey account' ($resp.account_id -eq $acctB)
Check 'respondent cannot set sentiment' ($null -eq $resp.sentiment_score)
Check 'invite from another survey rejected (400)' ((Call $null POST '/SurveyResponse' @{ survey_id = $sA.id; invite_id = $inv.id }).Status -eq 400)
Check 'unknown survey rejected (400)' ((Call $null POST '/SurveyResponse' @{ survey_id = 'nope' }).Status -eq 400)
$f = [uri]::EscapeDataString((@{ invite_id = $inv.id } | ConvertTo-Json -Compress))
Check 'anonymous resume by invite_id' (@((Call $null GET "/SurveyResponse?filter=$f").Json).Count -eq 1)
Check 'anonymous list without invite_id -> 401' ((Call $null GET '/SurveyResponse').Status -eq 401)
Check 'respondent can save answers' ((Call $null PUT "/SurveyResponse/$($resp.id)" @{ answers = @{ q1 = 'b' }; current_question_index = 1 }).Status -eq 200)
Check 'respondent can complete' ((Call $null PUT "/SurveyResponse/$($resp.id)" @{ status = 'completed'; completed_at = (Get-Date).ToUniversalTime().ToString('o') }).Status -eq 200)
Check 'respondent cannot edit after completion (409)' ((Call $null PUT "/SurveyResponse/$($resp.id)" @{ answers = @{ q1 = 'c' } }).Status -eq 409)
Check 'owner can still edit completed response' ((Call $bAdmin PUT "/SurveyResponse/$($resp.id)" @{ status = 'in_progress' }).Status -eq 200)
Check 'A cannot read B response (404)' ((Call $sys GET "/SurveyResponse/$($resp.id)").Status -eq 404)

# --- blacklist / suppression -------------------------------------------------
"opt-outs"
Check 'B opt-out created' ((Call $bAdmin POST '/EmailBlacklist' @{ email = 'optout@x.com' }).Status -eq 200)
Check 'B user shares account list' (@((Call $bUser GET '/EmailBlacklist').Json | Where-Object email -eq 'optout@x.com').Count -eq 1)
Check 'A does not see B opt-out' (@((Call $sys GET '/EmailBlacklist').Json).Count -eq 0)
Sql "INSERT EmailSuppression(id,email,reason) VALUES (LOWER(CONVERT(CHAR(36),NEWID())),'bounced@x.com','hard_bounce')" | Out-Null
$chk = (Call $sys POST '/EmailBlacklist/check' @{ emails = @('optout@x.com', 'bounced@x.com', 'fine@x.com') }).Json
Check 'A: global suppression applies, B opt-out does not' ((@($chk.blacklisted) -join ',') -eq 'bounced@x.com') "got $(@($chk.blacklisted) -join ',')"
$chk = (Call $bAdmin POST '/EmailBlacklist/check' @{ emails = @('optout@x.com', 'bounced@x.com', 'fine@x.com') }).Json
Check 'B: own opt-out + global' ((@($chk.blacklisted) | Sort-Object) -join ',' -eq 'bounced@x.com,optout@x.com')

# --- users / admin -----------------------------------------------------------
"users"
$users = (Call $bAdmin GET '/User').Json
Check 'B lists only B users' (@($users).Count -eq 2)
$bUserId = Sql "SELECT id FROM Users WHERE email='buser@test.local'"
$bAdminId = Sql "SELECT id FROM Users WHERE email='badmin@test.local'"
Check 'plain user cannot edit users (403)' ((Call $bUser PUT "/User/$bUserId" @{ full_name = 'x' }).Status -eq 403)
Check 'B admin cannot demote last B admin (409)' ((Call $bAdmin PUT "/User/$bAdminId" @{ role = 'user' }).Status -eq 409)
$sysId = Sql "SELECT id FROM Users WHERE email='sys@test.local'"
Check 'B admin cannot touch A user (404)' ((Call $bAdmin PUT "/User/$sysId" @{ full_name = 'x' }).Status -eq 404)
Check 'usage report: B admin forbidden (403)' ((Call $bAdmin GET '/admin/usage-report').Status -eq 403)
Check 'usage report: system admin ok' ((Call $sys GET '/admin/usage-report').Status -eq 200)

# --- deactivated user --------------------------------------------------------
Sql "UPDATE Users SET is_active=0 WHERE email='buser@test.local'" | Out-Null
Check 'deactivated user with live cookie -> 401' ((Call $bUser GET '/Survey').Status -eq 401)

""
if ($script:fails -eq 0) { "ALL PASSED" } else { "$($script:fails) FAILED" }
