# Run against a LOCAL copy of the API only: IIS Express on port 5391 serving
# the build with connectionStrings.config pointed at (localdb)\SFTest.
# The setup step DELETES data - never point it at a real DB.
$base = 'http://localhost:5391'
$cs = 'Server=(localdb)\SFTest;Database=SurveyFlow;Integrated Security=true'
$pw = 'Test-Pass-1234'
$script:fails = 0

function Sql($q) {
    $cn = New-Object System.Data.SqlClient.SqlConnection $cs; $cn.Open()
    $c = $cn.CreateCommand(); $c.CommandText = $q; $r = $c.ExecuteScalar(); $cn.Close(); return $r
}
function Call($session, $method, $path, $body = $null) {
    $a = @{ Uri = "$base$path"; Method = $method; SkipHttpErrorCheck = $true; ContentType = 'application/json' }
    if ($session) { $session.Headers.Clear(); $a.WebSession = $session }
    if ($null -ne $body) { $a.Body = (ConvertTo-Json -InputObject $body -Depth 10 -Compress) }
    $r = Invoke-WebRequest @a
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
function NewAccount($slug, $name, $mode) {
    return Sql "IF NOT EXISTS (SELECT 1 FROM Accounts WHERE slug='$slug') INSERT Accounts(id,name,slug,contact_match_key) VALUES (LOWER(CONVERT(CHAR(36),NEWID())),'$name','$slug','$mode'); SELECT id FROM Accounts WHERE slug='$slug'"
}
function NewUser($email, $acct, $role) {
    Sql "INSERT Users(id,email,password_hash,role,is_active,account_id,is_system_admin)
         SELECT LOWER(CONVERT(CHAR(36),NEWID())),'$email',password_hash,'$role',1,'$acct',0 FROM Users WHERE email='sys@test.local'" | Out-Null
}

# --- setup ---------------------------------------------------------------
Sql "DELETE FROM ContactNotes; DELETE FROM ContactGroupMembers; DELETE FROM ContactGroupSurveys; DELETE FROM ContactGroups;
     DELETE FROM SurveyResponses; DELETE FROM SurveyInvites; DELETE FROM Contacts; DELETE FROM ContactFields;
     DELETE FROM EmailBlacklist; DELETE FROM EmailSuppression; DELETE FROM AiUsage; DELETE FROM Users;
     DELETE FROM Surveys WHERE created_by LIKE '%@test.local'" | Out-Null
Call $null POST '/auth/seed-admin' @{ email = 'sys@test.local'; password = $pw } | Out-Null
$acctB = NewAccount 'client-b' 'Client B' 'email'
$acctC = NewAccount 'client-c' 'Client C (ID)' 'id_number'
NewUser 'badmin@test.local' $acctB 'admin'; NewUser 'buser@test.local' $acctB 'user'; NewUser 'cadmin@test.local' $acctC 'admin'
$sys = Login 'sys@test.local'; $b = Login 'badmin@test.local'; $bu = Login 'buser@test.local'; $c = Login 'cadmin@test.local'
Sql "INSERT EmailSuppression(id,email,reason) VALUES (LOWER(CONVERT(CHAR(36),NEWID())),'bounced@x.com','hard_bounce')" | Out-Null

# --- contacts ----------------------------------------------------------------
"contacts"
$pat = (Call $b POST '/Contact' @{ first_name = 'Pat'; last_name = 'Lee'; email = 'Pat.Lee@X.com'; company = 'Acme' }).Json
Check 'create contact' ($pat.id -and $pat.email -eq 'Pat.Lee@X.com')
$r = Call $b POST '/Contact' @{ email = 'pat.lee@x.com' }
Check 'duplicate email (case-insensitive) -> 409 with existing id' ($r.Status -eq 409 -and $r.Json.existing_id -eq $pat.id)
Check 'a_b@x.com and axb@x.com both allowed' ((Call $b POST '/Contact' @{ email = 'a_b@x.com' }).Status -eq 200 -and (Call $b POST '/Contact' @{ email = 'axb@x.com' }).Status -eq 200)
Check 'no email or ID -> 400' ((Call $b POST '/Contact' @{ first_name = 'X' }).Status -eq 400)
Check 'ID only (email mode) -> ok' ((Call $b POST '/Contact' @{ first_name = 'Paper'; id_number = 'P-1' }).Status -eq 200)
Check 'invalid email -> 400' ((Call $b POST '/Contact' @{ email = 'not-an-email' }).Status -eq 400)
$r = Call $b POST '/Contact' @{ email = 'bounced@x.com' }
Check 'globally suppressed -> 409 suppressed' ($r.Status -eq 409 -and $r.Json.reason -eq 'suppressed')
Check 'same email allowed in another account' ((Call $sys POST '/Contact' @{ email = 'pat.lee@x.com' }).Status -eq 200)
Check 'A cannot read B contact (404)' ((Call $sys GET "/Contact/$($pat.id)").Status -eq 404)
Check 'A cannot edit B contact (404)' ((Call $sys PUT "/Contact/$($pat.id)" @{ company = 'x' }).Status -eq 404)

$r = Call $b PUT "/Contact/$($pat.id)" @{ company = 'Acme Corp' }
$notes = (Call $b GET "/ContactNote?contact_id=$($pat.id)").Json
Check 'update writes history note' ($r.Status -eq 200 -and (@($notes) | Where-Object { $_.note -like "*company*Acme Corp*" }))
Check 'create wrote history note' (@($notes) | Where-Object note -eq 'Contact created.')

$s = (Call $b GET '/Contact/search?q=a_b').Json
Check 'search treats _ literally' ($s.total -eq 1 -and $s.items[0].email -eq 'a_b@x.com')
$s = (Call $b GET '/Contact/search?page_size=2&sort=email').Json
Check 'search paging: total + page size' ($s.total -eq 4 -and @($s.items).Count -eq 2)
Check 'B plain user can list contacts' ((Call $bu GET '/Contact/search').Json.total -eq 4)

Check 'deactivate' ((Call $b DELETE "/Contact/$($pat.id)").Status -eq 200)
Check 'deactivated hidden from search' ((Call $b GET '/Contact/search').Json.total -eq 3)
Check 'include_inactive shows it' ((Call $b GET '/Contact/search?include_inactive=true').Json.total -eq 4)
$pat2 = (Call $b POST '/Contact' @{ email = 'pat.lee@x.com'; first_name = 'Pat2' }).Json
Check 're-add same email after deactivation' ($pat2.id -and $pat2.id -ne $pat.id)
Check 'reactivating old one -> 409 duplicate' ((Call $b PUT "/Contact/$($pat.id)" @{ is_active = $true }).Status -eq 409)

# --- custom fields -------------------------------------------------------------
"custom fields"
Check 'plain user cannot define fields (403)' ((Call $bu POST '/ContactField' @{ label = 'Region' }).Status -eq 403)
$fRegion = (Call $b POST '/ContactField' @{ label = 'Region'; field_type = 'choice'; options = @('East', 'West') }).Json
Check 'field key from label' ($fRegion.field_key -eq 'region')
Check 'choice needs options (400)' ((Call $b POST '/ContactField' @{ label = 'Bad'; field_type = 'choice' }).Status -eq 400)
$fYears = (Call $b POST '/ContactField' @{ label = 'Years of Service'; field_type = 'number' }).Json
Check 'dup key -> 409' ((Call $b POST '/ContactField' @{ label = 'region' }).Status -eq 409)
$r = Call $b PUT "/Contact/$($pat2.id)" @{ custom = @{ region = 'east'; years_of_service = '12' } }
Check 'custom values normalised' ($r.Json.custom.region -eq 'East' -and $r.Json.custom.years_of_service -eq 12) ($r.Raw)
Call $b PUT "/Contact/$($pat2.id)" @{ company = 'KeepMe' } | Out-Null
Check 'bad choice -> 400' ((Call $b PUT "/Contact/$($pat2.id)" @{ custom = @{ region = 'North' } }).Status -eq 400)
Check 'unknown field -> 400' ((Call $b PUT "/Contact/$($pat2.id)" @{ custom = @{ shoe = '9' } }).Status -eq 400)
Check 'bad number -> 400' ((Call $b PUT "/Contact/$($pat2.id)" @{ custom = @{ years_of_service = 'ten' } }).Status -eq 400)
Check 'A cannot see B fields' (@((Call $sys GET '/ContactField').Json).Count -eq 0)

# --- groups ------------------------------------------------------------------
"groups"
$g1 = (Call $b POST '/ContactGroup' @{ name = 'Board' }).Json
Check 'create group' ($g1.id -and $g1.member_count -eq 0)
Check 'dup group name -> 409' ((Call $b POST '/ContactGroup' @{ name = 'Board' }).Status -eq 409)
$ids = @((Call $b GET '/Contact/search').Json.items | ForEach-Object id)
$aContact = (Call $sys GET '/Contact/search').Json.items[0].id
$r = (Call $b POST "/ContactGroup/$($g1.id)/members" @{ contact_ids = ($ids + $aContact) }).Json
Check 'add members (other account contact ignored)' ($r.added -eq 4 -and $r.ignored -eq 1)
Check 'member_count' ((Call $b GET "/ContactGroup/$($g1.id)").Json.member_count -eq 4)
Check 'adding again is a no-op' ((Call $b POST "/ContactGroup/$($g1.id)/members" @{ contact_ids = $ids }).Json.added -eq 0)
Check 'search by group' ((Call $b GET "/Contact/search?group_id=$($g1.id)").Json.total -eq 4)
Check 'remove one member' ((Call $b POST "/ContactGroup/$($g1.id)/members/remove" @{ contact_ids = @($ids[0]) }).Json.removed -eq 1)
Check 'other members untouched' ((Call $b GET "/ContactGroup/$($g1.id)").Json.member_count -eq 3)
$copy = (Call $b POST "/ContactGroup/$($g1.id)/copy" @{ name = 'Board copy' }).Json
Check 'copy keeps members + lineage' ($copy.member_count -eq 3 -and $copy.copied_from_group_id -eq $g1.id)
$g2 = (Call $b POST '/ContactGroup' @{ name = 'Staff' }).Json
$r = (Call $b PUT "/Contact/$($pat2.id)" @{ group_ids = @($g2.id) }).Json
Check 'contact group_ids sets exact set' (@($r.groups).Count -eq 1 -and $r.groups[0].name -eq 'Staff')
Check 'A cannot see B groups' (@((Call $sys GET '/ContactGroup').Json).Count -eq 0)
Check 'A cannot add to B group (404)' ((Call $sys POST "/ContactGroup/$($g1.id)/members" @{ contact_ids = @($aContact) }).Status -eq 404)
Check 'delete group (soft)' ((Call $b DELETE "/ContactGroup/$($copy.id)").Status -eq 204)
Check 'name reusable after delete' ((Call $b POST '/ContactGroup' @{ name = 'Board copy' }).Status -eq 200)

# --- import ------------------------------------------------------------------
"import"
$rows = @(
    @{ row = 2; first_name = 'New'; last_name = 'Person'; email = 'new1@x.com'; company = 'NewCo'; custom = @{ region = 'West' } },
    @{ row = 3; email = 'PAT.LEE@x.com'; last_name = 'Lee-Updated'; company = '' },
    @{ row = 4; email = 'dup@x.com'; first_name = 'First' },
    @{ row = 5; email = 'dup@x.com'; first_name = 'Second' },
    @{ row = 6; email = 'broken' },
    @{ row = 7; email = 'bounced@x.com' },
    @{ row = 8; email = 'odd@x.com'; custom = @{ shoe = '9' } },
    @{ row = 9; first_name = 'Nobody' }
)
$before = Sql "SELECT COUNT(*) FROM Contacts WHERE account_id='$acctB'"
$dry = (Call $b POST '/Contact/import' @{ rows = $rows; group_id = $g2.id; dry_run = $true }).Json
Check 'dry run counts' ($dry.added -eq 2 -and $dry.updated -eq 1 -and $dry.skipped -eq 5) "added=$($dry.added) updated=$($dry.updated) skipped=$($dry.skipped)"
Check 'dry run writes nothing' ((Sql "SELECT COUNT(*) FROM Contacts WHERE account_id='$acctB'") -eq $before)
$reasons = @($dry.skipped_rows | ForEach-Object { "$($_.row):$($_.reason)" }) -join ' | '
Check 'skip reasons reported' ($reasons -like '*4:Duplicate in file (row 5 used)*' -and $reasons -like '*6:Invalid email*' -and $reasons -like '*7:Opted out*' -and $reasons -like "*8:Unknown custom field 'shoe'*" -and $reasons -like '*9:Needs an email or ID number*') $reasons
$imp = (Call $b POST '/Contact/import' @{ rows = $rows; group_id = $g2.id }).Json
Check 'commit counts match preview' ($imp.added -eq 2 -and $imp.updated -eq 1 -and $imp.skipped -eq 5)
$patRow = (Call $b GET "/Contact/$($pat2.id)").Json
Check 'non-blank overwrote, blank did not erase' ($patRow.last_name -eq 'Lee-Updated' -and $patRow.company -eq 'KeepMe') ($patRow | ConvertTo-Json -Compress)
Check 'custom kept on update' ($patRow.custom.region -eq 'East')
Check 'last duplicate row won' ((Sql "SELECT first_name FROM Contacts WHERE email='dup@x.com' AND account_id='$acctB'") -eq 'Second')
Check 'imported rows added to group' ((Call $b GET "/ContactGroup/$($g2.id)").Json.member_count -eq 3)
Check 'import history notes' ((Sql "SELECT COUNT(*) FROM ContactNotes WHERE contact_id='$($pat2.id)' AND note LIKE 'Updated by import%'") -eq 1)
$again = (Call $b POST '/Contact/import' @{ rows = $rows }).Json
Check 're-import is idempotent' ($again.added -eq 0 -and $again.updated -eq 0 -and $again.unchanged -eq 3)
$noUpd = (Call $b POST '/Contact/import' @{ rows = @(@{ email = 'new1@x.com'; company = 'Changed' }); update_existing = $false }).Json
Check 'update_existing=false leaves contact alone' ($noUpd.unchanged -eq 1 -and (Sql "SELECT company FROM Contacts WHERE email='new1@x.com' AND account_id='$acctB'") -eq 'NewCo')
Check 'import into other account group -> 400' ((Call $sys POST '/Contact/import' @{ rows = $rows; group_id = $g2.id }).Status -eq 400)

# --- ID-number accounts ------------------------------------------------------
"id-number mode"
Check 'ID required (400)' ((Call $c POST '/Contact' @{ email = 'e1@x.com' }).Status -eq 400)
$e1 = (Call $c POST '/Contact' @{ email = 'shared@x.com'; id_number = 'E1' }).Json
Check 'two IDs may share an email' ((Call $c POST '/Contact' @{ email = 'shared@x.com'; id_number = 'E2' }).Status -eq 200)
Check 'duplicate ID -> 409' ((Call $c POST '/Contact' @{ email = 'other@x.com'; id_number = 'E1' }).Status -eq 409)
$imp = (Call $c POST '/Contact/import' @{ rows = @(@{ id_number = 'E1'; email = 'changed@x.com' }) }).Json
Check 'import matches on ID and updates email' ($imp.updated -eq 1 -and (Sql "SELECT email FROM Contacts WHERE id='$($e1.id)'") -eq 'changed@x.com')

# --- invites from contacts ---------------------------------------------------
"invites from contacts"
$survey = (Call $b POST '/Survey' @{ title = 'Pulse'; status = 'active' }).Json
Sql "UPDATE ContactFields SET survey_ids_json = '[""someother""]' WHERE id = '$($fYears.id)'" | Out-Null
$dry = (Call $b POST '/SurveyInvite/from-contacts' @{ survey_id = $survey.id; group_ids = @($g2.id); dry_run = $true }).Json
Check 'dry run creates nothing' ($dry.would_create -eq 3 -and (Sql "SELECT COUNT(*) FROM SurveyInvites WHERE survey_id='$($survey.id)'") -eq 0)
$r = (Call $b POST '/SurveyInvite/from-contacts' @{ survey_id = $survey.id; group_ids = @($g2.id) }).Json
Check 'invites created from group' ($r.created -eq 3)
$inv = (Call $b GET ("/SurveyInvite?filter=" + [uri]::EscapeDataString((@{ contact_id = $pat2.id } | ConvertTo-Json -Compress)))).Json[0]
Check 'invite snapshots contact (name, company, custom for this survey only)' ($inv.last_name -eq 'Lee-Updated' -and $inv.custom.region -eq 'East' -and $null -eq $inv.custom.years_of_service) ($inv | ConvertTo-Json -Compress)
Check 'invite has token + password' ($inv.token.Length -ge 32 -and $inv.password.Length -eq 6)
$r = (Call $b POST '/SurveyInvite/from-contacts' @{ survey_id = $survey.id; group_ids = @($g2.id) }).Json
Check 'second run skips already invited' ($r.created -eq 0 -and $r.already_invited -eq 3)
$r = (Call $b POST '/SurveyInvite/from-contacts' @{ survey_id = $survey.id; group_ids = @($g1.id); random_count = 1 }).Json
Check 'random_count picks N of the not-yet-invited' ($r.created -eq 1 -and $r.eligible -ge 1)
Call $b PUT "/Contact/$($pat2.id)" @{ email = 'pat.new@x.com' } | Out-Null
Check 'email change flows to unfinished invite' ((Sql "SELECT email FROM SurveyInvites WHERE id='$($inv.id)'") -eq 'pat.new@x.com')
Check 'A cannot invite to B survey (404)' ((Call $sys POST '/SurveyInvite/from-contacts' @{ survey_id = $survey.id; group_ids = @($g2.id) }).Status -eq 404)
$aSurvey = (Call $sys POST '/Survey' @{ title = 'A pulse' }).Json
Check 'B group ignored for A survey' ((Call $sys POST '/SurveyInvite/from-contacts' @{ survey_id = $aSurvey.id; group_ids = @($g2.id) }).Json.created -eq 0)
Check 'manual invite with other-account contact -> 400' ((Call $sys POST '/SurveyInvite' @{ survey_id = $aSurvey.id; email = 'q@x.com'; contact_id = $pat2.id }).Status -eq 400)

# --- notes -------------------------------------------------------------------
"notes"
$n = (Call $bu POST '/ContactNote' @{ contact_id = $pat2.id; note = 'Called, left voicemail' }).Json
Check 'add note' ($n.note_type -eq 'note' -and $n.created_by -eq 'buser@test.local')
$sysNote = Sql "SELECT TOP 1 id FROM ContactNotes WHERE contact_id='$($pat2.id)' AND note_type='system'"
Check 'system note undeletable (400)' ((Call $b DELETE "/ContactNote/$sysNote").Status -eq 400)
$n2 = (Call $b POST '/ContactNote' @{ contact_id = $pat2.id; note = 'admin note' }).Json
Check 'plain user cannot delete others note (403)' ((Call $bu DELETE "/ContactNote/$($n2.id)").Status -eq 403)
Check 'author can delete own note' ((Call $bu DELETE "/ContactNote/$($n.id)").Status -eq 204)
Check 'A cannot note B contact (404)' ((Call $sys POST '/ContactNote' @{ contact_id = $pat2.id; note = 'x' }).Status -eq 404)

""
if ($script:fails -eq 0) { "ALL PASSED" } else { "$($script:fails) FAILED" }
