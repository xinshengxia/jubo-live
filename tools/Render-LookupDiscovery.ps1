param(
    [string]$Exe = '',
    [string]$Output = '',
    [float]$Scale = 1,
    [switch]$Light,
    [switch]$Minimum
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Exe) { $Exe = Join-Path $root '看直播-mpv.exe' }
if (-not $Output) { $Output = Join-Path $root 'artifacts\lookup-discovery-preview.png' }
$flags = [Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
Add-Type -AssemblyName System.Drawing,System.Windows.Forms,System.Web.Extensions
$assembly = [Reflection.Assembly]::LoadFile([IO.Path]::GetFullPath($Exe))
$assembly.GetType('DpiAwareness').GetMethod('Enable',$flags).Invoke($null,@())
$fontArguments = [object[]]::new(1)
$fontPath = [string](Join-Path $root 'fonts')
$fontArguments[0] = $fontPath.PSObject.BaseObject
$assembly.GetType('UiFonts').GetMethod('Register',$flags).Invoke($null,$fontArguments)
$assembly.GetType('DpiLayout').GetField('Scale',$flags).SetValue($null,$Scale)
[Windows.Forms.Application]::EnableVisualStyles()
$assembly.GetType('ModernTheme').GetMethod('Apply',$flags).Invoke($null,@(-not $Light))
$hub = $assembly.GetType('LiveHub')
$type = $hub.GetNestedType('LookupUi',$flags)
$ui = [Activator]::CreateInstance($type,$flags,$null,@('all','职业外号',$null),$null)
$form = $type.GetField('Form',$flags).GetValue($ui)
try {
    if ($Minimum) { $form.Size = $form.MinimumSize }
    $form.StartPosition = 'Manual'; $form.Location = [Drawing.Point]::new(-20000,-20000); $form.ShowInTaskbar = $false; $form.Show()
    $list = $type.GetField('Candidates',$flags).GetValue($ui)
    $list.GetType().GetField('Grouped',$flags).SetValue($list,$true)
    $type.GetField('replacing',$flags).SetValue($ui,$true)
    $json = [Web.Script.Serialization.JavaScriptSerializer]::new()
    $rows = @(
        @{platform='bili';display_name='同名游戏主播';account_id='123';room_id='456';category='英雄联盟';description='公开账号简介';relevance='ambiguous';verification='unverified'},
        @{platform='huya';display_name='某公会丶Career-ID（职业选手）';account_id='124';room_id='457';category='英雄联盟';relevance='supported';relevance_note='职业经历与用户提供的游戏身份相符';verification='verified';account_verified=$true;room_url='https://www.huya.com/457';live_status='offline'},
        @{platform='douyu';display_name='另一个同名主播';account_id='125';room_id='458';category='其他游戏';relevance='conflict';relevance_note='公开简介的游戏领域与线索冲突';verification='unverified'},
        @{platform='douyin';display_name='找到账号，暂未取得数字房间';sec_uid='MS4wLjfixture';account_handle='FixtureHandle';profile_url='https://www.douyin.com/user/MS4wLjfixture';verification='account-only';account_verified=$true;description='同名账号单独保留';relevance='ambiguous'}
    )
    $selected = $null
    foreach ($row in $rows) {
        $row.sources = @(@{index='1';url='https://www.huya.com/457';title='合成官方资料（布局测试）';excerpt='此处展示真实来源摘录的位置；账号详情保留在上方。';retrieved_at=[DateTime]::UtcNow.ToString('o')})
        $row.match_reasons = @('合成线索，用于检查长文本显示')
        $row.verified_at = [DateTime]::UtcNow.ToString('o')
        $candidate = $json.Deserialize(($row|ConvertTo-Json -Depth 10),$hub.GetNestedType('LookupCandidate',$flags))
        $list.Items.Add($candidate)|Out-Null
        if ($row.platform -eq 'huya') { $selected = $candidate }
    }
    $list.SelectedIndex = 1
    $type.GetField('replacing',$flags).SetValue($ui,$false)
    $type.GetField('selected',$flags).SetValue($ui,$selected.PSObject.BaseObject)
    $type.GetMethod('Display',$flags).Invoke($ui,@($selected.PSObject.BaseObject))
    $type.GetField('Sources',$flags).GetValue($ui).SelectedIndex = 0
    $type.GetField('Name',$flags).GetValue($ui).Text = '职业选手'
    $type.GetField('Status',$flags).GetValue($ui).Text = '合成布局预览：四平台分组、匹配意见、独立账号阶段与来源摘录。'
    $type.GetMethod('UpdateAdd',$flags).Invoke($ui,@())
    $form.PerformLayout()
    [Windows.Forms.Application]::DoEvents()
    $bitmap = [Drawing.Bitmap]::new($form.Width,$form.Height)
    try { $form.DrawToBitmap($bitmap,[Drawing.Rectangle]::new(0,0,$bitmap.Width,$bitmap.Height)); $bitmap.Save([IO.Path]::GetFullPath($Output)) }
    finally { $bitmap.Dispose() }
    Write-Output $Output
}
finally { $ui.Dispose() }
