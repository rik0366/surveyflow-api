# Tests sql/10_mailing_lists_to_groups.sql against the LOCAL test DB
# ((localdb)\SFTest). Run tests/addressbook_test.ps1 first (it seeds
# accounts, contacts, groups and suppression). Never point at a real DB.
$cs = 'Server=(localdb)\SFTest;Database=SurveyFlow;Integrated Security=true'
$script:fails = 0
function Sql($q) { $cn = New-Object System.Data.SqlClient.SqlConnection $cs; $cn.Open(); $c = $cn.CreateCommand(); $c.CommandText = $q; $r = $c.ExecuteScalar(); $cn.Close(); return $r }
function Check($name, $cond, $detail = '') { if ($cond) { "  PASS  $name" } else { "  FAIL  $name  $detail"; $script:fails++ } }
function RunScript() {
    $text = Get-Content "$PSScriptRoot\..\sql\10_mailing_lists_to_groups.sql" -Raw
    $cn = New-Object System.Data.SqlClient.SqlConnection ($cs -replace 'Database=SurveyFlow', 'Database=master'); $cn.Open()
    $out = @()
    foreach ($b in ([regex]::Split($text, '(?im)^\s*GO\s*$') | Where-Object { $_.Trim() })) {
        $c = $cn.CreateCommand(); $c.CommandText = $b
        $da = New-Object System.Data.SqlClient.SqlDataAdapter $c; $t = New-Object System.Data.DataTable; [void]$da.Fill($t)
        if ($t.Rows.Count) { $out += $t.Rows }
    }
    $cn.Close(); return $out
}

$acctB = Sql "SELECT id FROM Accounts WHERE slug='client-b'"
$acctC = Sql "SELECT id FROM Accounts WHERE slug='client-c'"
Sql "DELETE FROM MailingLists" | Out-Null
Sql "INSERT MailingLists(id,name,description,emails_json,created_by,account_id) VALUES
 ('ml-b-board','Board','old board list','[""pat.new@x.com"",""PAT.NEW@x.com "",""newperson@list.com"",""bounced@x.com"",""not-an-email"",""a b@x.com""]','badmin@test.local','$acctB'),
 ('ml-b-new','Volunteers',NULL,'[""vol1@list.com"",""vol2@list.com""]','badmin@test.local','$acctB'),
 ('ml-c','C list',NULL,'[""shared@x.com"",""nobody@list.com""]','cadmin@test.local','$acctC')" | Out-Null
$contactsBefore = Sql "SELECT COUNT(*) FROM Contacts WHERE account_id='$acctB'"

"first run"
$rep = @(RunScript)
$rep | ForEach-Object { "    {0} -> {1}: emails={2} matched={3} new={4} invalid={5} suppressed={6} no_id={7}" -f $_.list_name, $_.group_name, $_.emails, $_.matched_existing, $_.new_contacts, $_.skipped_invalid, $_.skipped_suppressed, $_.skipped_no_id }
$board = $rep | Where-Object list_name -eq 'Board'
Check 'dup emails counted once (6 entries -> 5 distinct)' ($board.emails -eq 5)
Check 'existing contact matched' ($board.matched_existing -eq 1)
Check 'new contact created' ($board.new_contacts -eq 1)
Check 'invalid skipped (no @, space)' ($board.skipped_invalid -eq 2)
Check 'suppressed skipped' ($board.skipped_suppressed -eq 1)
Check 'name clash -> "(mailing list)"' ($board.group_name -eq 'Board (mailing list)')
Check 'group has matched + new members' ((Sql "SELECT COUNT(*) FROM ContactGroupMembers m JOIN ContactGroups g ON g.id=m.group_id WHERE g.name='Board (mailing list)' AND g.account_id='$acctB'") -eq 2)
Check 'contacts created in B' ((Sql "SELECT COUNT(*) FROM Contacts WHERE account_id='$acctB'") -eq $contactsBefore + 3)
Check 'new contact has match_key + history note' ((Sql "SELECT match_key FROM Contacts WHERE email='newperson@list.com'") -eq 'newperson@list.com' -and (Sql "SELECT COUNT(*) FROM ContactNotes n JOIN Contacts c ON c.id=n.contact_id WHERE c.email='newperson@list.com' AND n.note LIKE '%mailing list%'") -eq 1)
$c = $rep | Where-Object list_name -eq 'C list'
Check 'ID account: existing email matched, missing one skipped' ($c.matched_existing -eq 1 -and $c.skipped_no_id -eq 1 -and $c.new_contacts -eq 0)
Check 'lists marked converted' ((Sql "SELECT COUNT(*) FROM MailingLists WHERE converted_group_id IS NULL") -eq 0)

"second run (idempotent)"
$rep2 = @(RunScript)
Check 'nothing converted twice' (@($rep2).Count -eq 0 -and (Sql "SELECT COUNT(*) FROM ContactGroups WHERE name LIKE 'Volunteers%'") -eq 1)

"new list after conversion"
Sql "INSERT MailingLists(id,name,emails_json,created_by,account_id) VALUES ('ml-b-late','Volunteers','[""vol1@list.com""]','badmin@test.local','$acctB')" | Out-Null
$rep3 = @(RunScript)
Check 'late list converted, reuses existing contact, free name' ($rep3[0].matched_existing -eq 1 -and $rep3[0].group_name -eq 'Volunteers (mailing list)')

""
if ($script:fails -eq 0) { "ALL PASSED" } else { "$($script:fails) FAILED" }
