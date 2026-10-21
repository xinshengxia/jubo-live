using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

internal static partial class LiveHub
{
    private static readonly LookupService SharedLookupService = new LookupService();
    private static readonly LookupVerifier SharedLookupVerifier = new LookupVerifier();
    private static void ShowLookup(Form owner, Action<LookupCandidate, string> save)
    {
        using (var ui = new LookupUi("all", "", save, SharedLookupService, SharedLookupVerifier)) ui.Form.ShowDialog(owner);
    }

    internal sealed class LookupUi : IDisposable
    {
        internal readonly Form Form;
        internal readonly Control View;
        private readonly bool ownsForm;
        internal readonly ComboBox Platform = new ModernComboBox();
        internal readonly ModernInput Input = new ModernInput(false);
        internal readonly Button Search = new ModernButton(), Cancel = new ModernButton(), Deep = new ModernButton(), ContinueAi = new ModernButton();
        internal readonly Button Add = new ModernButton(), SourcesButton = new ModernButton(), Web = new ModernButton(), Config = new ModernButton();
        internal readonly Button Resolve = new ModernButton(), OpenRoom = new ModernButton(), Refresh = new ModernButton(), OpenProfile = new ModernButton(), Clues = new ModernButton();
        internal readonly Button DouyinBrowser = new ModernButton();
        internal Func<IWin32Window, string, LookupResult> BrowserSearch = DouyinLookupBrowser.Show;
        internal readonly ModernInput SourcePreview = new ModernInput(true);
        internal readonly ListBox Candidates = new LookupCandidateList(), Sources = new ModernListBox();
        internal readonly Dictionary<string, ModernButton> PlatformFilters = new Dictionary<string, ModernButton>();
        private readonly List<LookupCandidate> allCandidates = new List<LookupCandidate>();
        private LookupCandidate aiRecommendation;
        private string resultPlatform = "all";
        private readonly ModernInput detailsInput = new ModernInput(true), nameInput = new ModernInput(false);
        internal readonly TextBox Details;
        internal readonly Label Status = new ModernLabel();
        internal readonly TextBox Name;
        internal readonly LookupService Service;
        internal readonly LookupVerifier Verifier;
        private readonly LookupGeneration queries = new LookupGeneration(), selections = new LookupGeneration();
        private readonly Action<LookupCandidate, string> save;
        private CancellationTokenSource queryCancel, verifyCancel;
        private LookupCandidate selected;
        private bool replacing, closed;
        private LookupResult lastResult;
        private string shownQueryContext;
        internal LookupUi(string platform, string initial, Action<LookupCandidate, string> onSave)
            : this(platform, initial, onSave, new LookupService(), new LookupVerifier()) { }
        internal LookupUi(string platform, string initial, Action<LookupCandidate, string> onSave, LookupService service, LookupVerifier verifier)
            : this(platform, initial, onSave, service, verifier, null) { }
        internal LookupUi(string platform, string initial, Action<LookupCandidate, string> onSave, LookupService service, LookupVerifier verifier, Form owner)
        {
            ownsForm = owner == null; Form = owner ?? new DpiForm();
            save = onSave; Service = service; Verifier = verifier;
            if (ownsForm)
            {
                Form.Name = "streamerLookup"; Form.Text = "查找主播"; DpiLayout.SetDesignFont(Form, ModernTheme.UiFont);
                Form.BackColor = ModernTheme.Surface; Form.ForeColor = ModernTheme.Text; Form.StartPosition = FormStartPosition.CenterParent;
                Form.HandleCreated += delegate { ModernTheme.ApplyWindowChrome(Form); };
            }
            Details = detailsInput.Inner; Name = nameInput.Inner;
            if (ownsForm)
            {
                Form.ClientSize = new Size(DpiLayout.S(980), DpiLayout.S(660));
                Form.MinimumSize = new Size(DpiLayout.S(820) + Form.Width - Form.ClientSize.Width,
                    DpiLayout.S(590) + Form.Height - Form.ClientSize.Height);
                Form.AutoScaleMode = AutoScaleMode.None;
            }
            ModernTheme.Changed += FollowTheme;
            Form.Disposed += delegate { ModernTheme.Changed -= FollowTheme; };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(DpiLayout.S(12)), ColumnCount = 1, RowCount = 6 };
            View = layout;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, DpiLayout.S(42)));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, DpiLayout.S(52)));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var inputRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = new Padding(0, 0, 0, DpiLayout.S(8)) };
            inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, DpiLayout.S(100))); inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, DpiLayout.S(86))); inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, DpiLayout.S(86)));
            inputRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Platform.Name = "lookupPlatform"; Platform.DropDownStyle = ComboBoxStyle.DropDownList; Platform.Dock = DockStyle.Fill;
            Platform.Items.AddRange(new object[] { "B站", "虎牙", "斗鱼", "抖音", "全部平台" }); Platform.SelectedIndex = Array.IndexOf(new[] { "bili", "huya", "douyu", "douyin", "all" }, platform);
            if (Platform.SelectedIndex < 0) Platform.SelectedIndex = 4;
            ((IDpiScaled)Platform).OnDpiScaled(DpiLayout.Scale); Platform.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            // A native ComboBox ignores Height. Adjust its owner-drawn item height
            // so its actual height matches the other controls in this row.
            Platform.ItemHeight = Math.Max(1, Platform.ItemHeight + DpiLayout.S(34) - Platform.Height);
            Platform.Margin = Input.Margin = Search.Margin = new Padding(0, 0, DpiLayout.S(6), 0); Cancel.Margin = Padding.Empty;
            Input.Name = "lookupInput"; Input.Size = new Size(DpiLayout.S(200), DpiLayout.S(34)); Input.Dock = DockStyle.Fill; Input.Text = initial ?? "";
            inputRow.Controls.Add(Platform, 0, 0); inputRow.Controls.Add(Input, 1, 0); layout.Controls.Add(inputRow, 0, 0);
            var actions = Flow();
            SetButton(Search, "查询", "lookupSearch"); SetButton(Cancel, "取消", "lookupCancel");
            SetButton(Deep, "AI 优化现有结果", "lookupOptimize"); SetButton(ContinueAi, "AI 重新搜索", "lookupContinueAi");
            SetButton(Refresh, "刷新结果", "lookupRefresh"); SetButton(Clues, "补充线索", "lookupClues");
            SetButton(Web, "平台网页搜索", "lookupWeb"); SetButton(Config, "DeepSeek 设置", "lookupConfig");
            SetButton(DouyinBrowser, "抖音网页辅助", "lookupDouyinBrowser");
            Cancel.Enabled = false; Deep.Enabled = false;
            Search.AutoSize = Cancel.AutoSize = false; Search.Dock = Cancel.Dock = DockStyle.Fill; ((ModernButton)Search).Style = ModernButton.Kind.Primary;
            Search.Margin = new Padding(0, 0, DpiLayout.S(6), 0); Cancel.Margin = Padding.Empty;
            inputRow.Controls.Add(Search, 2, 0); inputRow.Controls.Add(Cancel, 3, 0);
            actions.Controls.AddRange(new Control[] { Refresh, Deep, ContinueAi, Clues, DouyinBrowser, Web, Config }); layout.Controls.Add(actions, 0, 1);
            Status.Name = "lookupStatus"; Status.Dock = DockStyle.Fill; Status.Margin = Padding.Empty; Status.TextAlign = ContentAlignment.MiddleLeft;
            Status.Text = "输入昵称、游戏身份、房间链接或分享文案；抖音号可写为“抖音号：123”。选中候选后核验。";
            ((ModernLabel)Status).Role = ModernLabel.RoleKind.Secondary; layout.Controls.Add(Status, 0, 2);
            var filters = Flow(); filters.Name = "lookupResultFilters";
            filters.Controls.Add(new ModernLabel { Text = "结果平台", AutoSize = false,
                Size = new Size(DpiLayout.S(68), DpiLayout.S(34)), TextAlign = ContentAlignment.MiddleLeft });
            foreach (string key in new[] { "all", "bili", "huya", "douyu", "douyin" })
            {
                string filter = key; var button = new ModernButton();
                SetButton(button, key == "all" ? "全部 (0)" : Platforms.Name(key) + " (0)", "lookupFilter_" + key);
                button.Click += delegate { FilterResults(filter); };
                button.Tab = true;
            PlatformFilters[key] = button; filters.Controls.Add(button);
            }
            UpdateFilters(); layout.Controls.Add(filters, 0, 3);
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Candidates.Name = "lookupCandidates"; Candidates.Dock = DockStyle.Fill; Candidates.IntegralHeight = false;
            var candidateSection = Section("候选主播 · 选择后核验", Candidates); candidateSection.Name = "lookupCandidatesCard";
            candidateSection.Margin = new Padding(0, 0, DpiLayout.S(12), 0);
            body.Controls.Add(candidateSection, 0, 0);
            Candidates.HorizontalScrollbar = false; Sources.HorizontalScrollbar = true;
            Candidates.BorderStyle = Sources.BorderStyle = BorderStyle.None;
            ((ModernListBox)Sources).DrawBorder = false;
            ((IDpiScaled)Candidates).OnDpiScaled(DpiLayout.Scale); ((IDpiScaled)Sources).OnDpiScaled(DpiLayout.Scale);
            Details.Name = "lookupDetails"; Details.ReadOnly = true; Details.WordWrap = true;
            Details.Text = "先输入主播名称或链接，再点击查询。\r\n\r\n选择候选后，这里显示账号、匹配理由与平台核验结果。\r\n候选不会自动选中或加入收藏。";
            detailsInput.Dock = DockStyle.Fill;
            Sources.Name = "lookupSources"; Sources.Dock = DockStyle.Fill; Sources.IntegralHeight = false;
            var evidence = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
            evidence.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); evidence.RowStyles.Add(new RowStyle(SizeType.Percent, 25)); evidence.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
            var detailsSection = Section("账号详情与核验", detailsInput); detailsSection.Name = "lookupDetailsCard";
            evidence.Controls.Add(detailsSection, 0, 0);
            var sourceSection = Section("搜索来源 · 选择可查看摘录", Sources); sourceSection.Name = "lookupSourcesCard";
            sourceSection.Margin = new Padding(0, DpiLayout.S(10), 0, 0);
            evidence.Controls.Add(sourceSection, 0, 1);
            SourcePreview.Name = "lookupSourcePreview"; SourcePreview.Inner.ReadOnly = true; SourcePreview.Inner.WordWrap = true; SourcePreview.Dock = DockStyle.Fill;
            SourcePreview.Inner.Text = "选择来源查看摘录，账号详情会保留。";
            var previewSection = Section("所选来源摘录", SourcePreview); previewSection.Name = "lookupSourcePreviewCard";
            previewSection.Margin = new Padding(0, DpiLayout.S(10), 0, 0);
            evidence.Controls.Add(previewSection, 0, 2);
            var evidenceViewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
            evidence.Dock = DockStyle.Top; evidenceViewport.Controls.Add(evidence);
            bool layingEvidence = false;
            Action layoutEvidence = delegate
            {
                if (layingEvidence) return;
                layingEvidence = true;
                try
                {
                    int height = Math.Max(evidenceViewport.ClientSize.Height, DpiLayout.S(Form, 460));
                    evidence.Height = height;
                    evidenceViewport.AutoScrollMinSize = new Size(0, height);
                }
                finally { layingEvidence = false; }
            };
            evidenceViewport.HandleCreated += delegate { ModernTheme.ApplyScrollbarTheme(evidenceViewport); };
            Action evidenceTheme = delegate { if (!evidenceViewport.IsDisposed) ModernTheme.ApplyScrollbarTheme(evidenceViewport); };
            ModernTheme.Changed += evidenceTheme;
            evidenceViewport.Disposed += delegate { ModernTheme.Changed -= evidenceTheme; };
            evidenceViewport.SizeChanged += delegate { layoutEvidence(); };
            ((DpiForm)Form).DpiUpdated += delegate { layoutEvidence(); };
            body.Controls.Add(evidenceViewport, 1, 0); layout.Controls.Add(body, 0, 4);
            var addRow = Flow(); Name.Name = "lookupFavoriteName"; nameInput.Size = new Size(DpiLayout.S(220), DpiLayout.S(34));
            addRow.Margin = new Padding(0, DpiLayout.S(8), 0, 0);
            nameInput.Margin = new Padding(0, 0, DpiLayout.S(6), DpiLayout.S(6));
            SetButton(Add, "加入收藏", "lookupAdd"); SetButton(SourcesButton, "查看所选来源", "lookupOpenSource"); Add.Enabled = false;
            SetButton(OpenProfile, "账号主页", "lookupOpenProfile");
            SetButton(Resolve, "重新解析房间", "lookupResolveRoom"); SetButton(OpenRoom, "打开直播间", "lookupOpenRoom");
            Resolve.Enabled = OpenRoom.Enabled = OpenProfile.Enabled = false;
            var nameLabel = new ModernLabel { Text = "收藏名称", AutoSize = false, Size = new Size(DpiLayout.S(68), DpiLayout.S(34)),
                TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 0, DpiLayout.S(6), DpiLayout.S(6)) };
            nameLabel.Width = Math.Max(nameLabel.Width, TextRenderer.MeasureText(nameLabel.Text, nameLabel.Font).Width + DpiLayout.S(8));
            addRow.Controls.AddRange(new Control[] { nameLabel, nameInput, Add, Resolve, OpenRoom, OpenProfile, SourcesButton }); layout.Controls.Add(addRow, 0, 5);
            // Editable inputs draw one matching border; read-only evidence shares
            // the surrounding card surface instead of nesting a second outline.
            Input.CornerRadius = nameInput.CornerRadius = detailsInput.CornerRadius = SourcePreview.CornerRadius = 8;
            detailsInput.DrawBorder = SourcePreview.DrawBorder = false;
            Control[] roundedControls = new Control[] { Candidates, Sources };
            for (int i = 0; i < roundedControls.Length; i++)
            {
                Control rounded = roundedControls[i];
                rounded.SizeChanged += delegate { RoundLookupControl(rounded, 8); };
                rounded.HandleCreated += delegate { RoundLookupControl(rounded, 8); };
            }
            Action roundAll = delegate
            {
                for (int i = 0; i < roundedControls.Length; i++) RoundLookupControl(roundedControls[i], 8);
            };
            ((DpiForm)Form).DpiUpdated += delegate { roundAll(); };
            if (ownsForm) Form.Shown += delegate { roundAll(); };
            if (View.IsHandleCreated) roundAll(); else View.HandleCreated += delegate { roundAll(); };
            if (ownsForm) { Form.Controls.Add(layout); Form.AcceptButton = Search; Form.CancelButton = Cancel; }
            Refresh.Click += delegate { BeginSearchCore(false, false, null, true); };
            Clues.Click += delegate { ShowClues(); };
            OpenProfile.Click += delegate { if (selected != null && OpenProfile.Enabled) OpenPublicUrl(LookupDiscovery.AccountPage(selected)); };
            Search.Click += delegate { BeginSearch(false, false); }; Deep.Click += delegate { BeginOptimize(); };
            ContinueAi.Click += delegate { BeginSearch(false, true); }; Cancel.Click += delegate { CancelWork("已取消；收藏未变更，已有候选保留。"); };
            Input.TextChanged += delegate { Reset("输入已改变，请点击查询。"); }; Platform.SelectedIndexChanged += delegate { Reset("平台已改变，请重新查询。"); };
            Candidates.SelectedIndexChanged += delegate { SelectCandidate(); };
            Name.TextChanged += delegate { UpdateAdd(); }; Sources.DoubleClick += delegate { OpenSource(); }; SourcesButton.Click += delegate { OpenSource(); };
            Resolve.Click += delegate { if (Candidates.SelectedItem != null) VerifySelectedCandidate(true); };
            OpenRoom.Click += delegate { if (selected != null && OpenRoom.Enabled) OpenPublicUrl(selected.room_url); };
            Sources.SelectedIndexChanged += delegate
            {
                var item = Sources.SelectedItem as SourceItem; if (item == null) return;
                SourcePreview.Inner.Text = item.Source.title + "\r\n" + item.Source.url + "\r\n来源日期：" + (item.Source.published_at ?? "未提供；不能用检索时间推断发布时间") + "\r\n检索时间：" + item.Source.retrieved_at
                    + "\r\n" + (item.Source.excerpt ?? "此来源未提供摘录。") + "\r\n支持字段："
                    + string.Join("；", item.Source.supports ?? new string[0]);
            };
            Web.Click += delegate { OpenPlatformSearch(); };
            Config.Click += delegate { ShowDeepSeekSettings(Form); Service.ClearCache(); };
            DouyinBrowser.Click += delegate { BeginBrowserSearch(); };
            Add.Click += delegate
            {
                if (selected == null || !Add.Enabled) return;
                var candidate = LookupData.Copy(selected); candidate.user_confirmed = true;
                try { save(candidate, Name.Text); Status.Text = "收藏操作已完成，可回到启动器播放。"; Add.Enabled = false; }
                catch (Exception ex) { Status.Text = "收藏失败：" + ex.Message; }
            };
            Form.FormClosing += delegate { CloseLookupWork(); };
            View.Disposed += delegate { CloseLookupWork(); };
            ((DpiForm)Form).DpiUpdated += delegate
            {
                int targetHeight = DpiLayout.S(Form, 34);
                Platform.ItemHeight = Math.Max(1, Platform.ItemHeight + targetHeight - Platform.Height);
                UpdateFilters();
                inputRow.PerformLayout();
            };
            if (ownsForm) Form.Shown += delegate { Input.Focus(); };
        }
        private void FollowTheme()
        {
            Form.BackColor = ModernTheme.Surface; Form.ForeColor = ModernTheme.Text;
            ModernTheme.ApplyWindowChrome(Form);
        }
        private static Control Section(string title, Control content)
        {
            var card = new WorkbenchPanel { IsCard = true, Dock = DockStyle.Fill,
                Padding = new Padding(DpiLayout.S(12)), Margin = Padding.Empty };
            var section = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
                Margin = Padding.Empty, Padding = Padding.Empty };
            section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            section.RowStyles.Add(new RowStyle(SizeType.Absolute, DpiLayout.S(26))); section.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var heading = ModernTheme.SectionHeader(title); heading.Dock = DockStyle.Fill; heading.TextAlign = ContentAlignment.MiddleLeft;
            heading.Margin = content.Margin = Padding.Empty;
            section.Controls.Add(heading, 0, 0); section.Controls.Add(content, 0, 1);
            card.Controls.Add(section); return card;
        }
        private static FlowLayoutPanel Flow()
        {
            return new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true, Margin = Padding.Empty, Padding = Padding.Empty };
        }
        private static void SetButton(Button button, string text, string name)
        {
            button.Name = name; button.Text = text; button.AutoSize = false;
            button.Size = new Size(Math.Max(DpiLayout.S(80), TextRenderer.MeasureText(text, button.Font).Width + DpiLayout.S(16)), DpiLayout.S(34));
            button.MinimumSize = new Size(DpiLayout.S(80), DpiLayout.S(34));
            button.Padding = new Padding(DpiLayout.S(8), DpiLayout.S(3), DpiLayout.S(8), DpiLayout.S(3)); button.Margin = new Padding(0, 0, DpiLayout.S(6), DpiLayout.S(6));
        }
        // Clip one control to a rounded rectangle. The lookup page uses the shared
        // ModernInput/ListBox classes, which paint their own square corners; a window
        // Region gives the draft's 8px result/field shape without changing their API
        // or control names. Radius is logical and tracks the window DPI.
        private static void RoundLookupControl(Control control, int radius)
        {
            if (control == null || control.IsDisposed || control.Width <= 2 || control.Height <= 2) return;
            using (GraphicsPath path = ModernTheme.RoundRect(new Rectangle(0, 0, control.Width, control.Height), DpiLayout.S(control, radius)))
            {
                Region previous = control.Region;
                control.Region = new Region(path);
                if (previous != null) previous.Dispose();
            }
        }
        private string CurrentPlatform()
        {
            string[] platforms = { "bili", "huya", "douyu", "douyin", "all" };
            int index = Platform.SelectedIndex;
            return index >= 0 && index < platforms.Length ? platforms[index] : "all";
        }
        private void UpdateFilters()
        {
            if (closed || Form.Disposing || View.Disposing || View.IsDisposed) return;
            DouyinBrowser.Enabled = queryCancel == null && (CurrentPlatform() == "all" || CurrentPlatform() == "douyin");
            foreach (var pair in PlatformFilters)
            {
                int count = pair.Key == "all" ? allCandidates.Count : allCandidates.FindAll(delegate(LookupCandidate c) { return c.platform == pair.Key; }).Count;
                pair.Value.Text = (pair.Key == "all" ? "全部" : Platforms.Name(pair.Key)) + " (" + count + ")";
                pair.Value.Selected = pair.Key == resultPlatform;
                pair.Value.Enabled = queryCancel == null && (pair.Key == "all" || count > 0);
                pair.Value.Width = Math.Max(DpiLayout.S(Form, 80), TextRenderer.MeasureText(pair.Value.Text, pair.Value.Font).Width + DpiLayout.S(Form, 16));
            }
        }
        internal void FilterResults(string platform)
        {
            if (queryCancel != null || !LookupDiscovery.KnownScope(platform)) return;
            resultPlatform = platform;
            selections.Next(); if (verifyCancel != null) verifyCancel.Cancel(); verifyCancel = null; selected = null;
            Add.Enabled = Resolve.Enabled = OpenRoom.Enabled = OpenProfile.Enabled = Cancel.Enabled = false;
            ShowResults(); Sources.Items.Clear();
            if (lastResult != null) foreach (LookupSource source in lastResult.sources) Sources.Items.Add(new SourceItem(source));
            Name.Text = ""; Details.Text = "请选择候选查看账号与来源。"; SourcePreview.Inner.Text = "选择来源查看摘录。";
            Status.Text = aiRecommendation == null
                ? "显示" + (platform == "all" ? "全部平台" : Platforms.Name(platform)) + " " + Candidates.Items.Count
                    + " 个候选（共 " + allCandidates.Count + " 个）；切换平台只筛选本次结果，无需重新搜索。"
                : "AI 优先候选固定置顶；其余按" + (platform == "all" ? "全部平台" : Platforms.Name(platform))
                    + "筛选，共显示 " + Candidates.Items.Count + " 个候选。";
        }
        private void ShowResults()
        {
            replacing = true; Candidates.BeginUpdate();
            try
            {
                Candidates.Items.Clear(); ((LookupCandidateList)Candidates).Grouped = false;
                ((LookupCandidateList)Candidates).Recommendation = aiRecommendation;
                if (aiRecommendation != null) Candidates.Items.Add(aiRecommendation);
                foreach (LookupCandidate item in allCandidates)
                    if (!ReferenceEquals(item, aiRecommendation) && (resultPlatform == "all" || item.platform == resultPlatform)) Candidates.Items.Add(item);
                Candidates.SelectedIndex = -1;
                if (Candidates.Items.Count > 0) Candidates.TopIndex = 0;
            }
            finally { Candidates.EndUpdate(); replacing = false; }
            UpdateFilters(); Deep.Enabled = allCandidates.Count > 0;
        }
        private void Ui(Action action)
        {
            if (closed || Form.IsDisposed || !Form.IsHandleCreated) return;
            try { Form.BeginInvoke((MethodInvoker)delegate { if (!closed && !Form.IsDisposed) action(); }); } catch (InvalidOperationException) { return; }
        }
        private void Reset(string note)
        {
            if (closed || Form.Disposing || View.Disposing || View.IsDisposed) return;
            CancelWork(note);
            replacing = true; Candidates.Items.Clear(); Sources.Items.Clear(); replacing = false;
            allCandidates.Clear(); aiRecommendation = null; ((LookupCandidateList)Candidates).Recommendation = null;
            resultPlatform = "all"; UpdateFilters();
            lastResult = null; Details.Text = "输入昵称或链接后查询；也可补充游戏、战队或曾用名。"; SourcePreview.Inner.Text = "";
            shownQueryContext = null;
            Deep.Enabled = false;
            if (note != null) Status.Text = note;
        }
        private void CancelWork(string note)
        {
            queries.Next(); selections.Next(); if (queryCancel != null) queryCancel.Cancel(); if (verifyCancel != null) verifyCancel.Cancel();
            queryCancel = null; verifyCancel = null; selected = null;
            replacing = true; Candidates.SelectedIndex = -1; replacing = false;
            Add.Enabled = false; Cancel.Enabled = false; Resolve.Enabled = OpenRoom.Enabled = OpenProfile.Enabled = false;
            Candidates.Enabled = true; Deep.Enabled = allCandidates.Count > 0;
            UpdateFilters();
            if (note != null) Status.Text = note;
        }
        internal void BeginSearch(bool deep, bool forceAi)
        {
            BeginSearchCore(false, forceAi, null);
        }
        internal void BeginOptimize()
        {
            var current = new List<LookupCandidate>();
            foreach (LookupCandidate candidate in allCandidates) current.Add(LookupData.Copy(candidate));
            if (current.Count == 0) { Status.Text = "请先查询得到候选，再优化现有结果。"; return; }
            BeginSearchCore(false, true, current);
        }
        private void BeginSearchCore(bool deep, bool forceAi, List<LookupCandidate> current) { BeginSearchCore(deep, forceAi, current, false); }
        private void BeginSearchCore(bool deep, bool forceAi, List<LookupCandidate> current, bool refresh)
        {
            if (forceAi) CancelWork("正在使用 DeepSeek，可随时取消；已有候选保留…");
            else CancelWork(refresh ? "正在重新检索，已有候选保留…" : "正在查询平台与公开索引…");
            int generation = queries.Next(); string platform = CurrentPlatform(), text = Input.Text;
            string context = platform + "\n" + text.Trim();
            if (shownQueryContext != context)
            {
                allCandidates.Clear(); aiRecommendation = null; lastResult = null;
                ShowResults(); Sources.Items.Clear(); SourcePreview.Inner.Text = "";
                Details.Text = "正在查询新的昵称或平台；上次候选不会用于本次查询。";
            }
            shownQueryContext = context;
            var cancellation = new CancellationTokenSource(); queryCancel = cancellation; Cancel.Enabled = true;
            UpdateFilters();
            Candidates.Enabled = false; Deep.Enabled = false;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    LookupResult result = current != null ? Service.Improve(platform, text, current, cancellation.Token, delegate(string message)
                        { Ui(delegate { if (queries.Current(generation)) Status.Text = message; }); })
                        : refresh ? Service.Refresh(platform, text, cancellation.Token, delegate(string message)
                        { Ui(delegate { if (queries.Current(generation)) Status.Text = message; }); })
                        : Service.Search(platform, text, deep, forceAi, cancellation.Token, delegate(string message)
                        { Ui(delegate { if (queries.Current(generation)) Status.Text = message; }); });
                    Ui(delegate
                    {
                        if (!queries.Current(generation) || cancellation.IsCancellationRequested) return;
                        queryCancel = null; Candidates.Enabled = true; lastResult = result;
                        bool retained = result.candidates.Count == 0 && allCandidates.Count > 0;
                        if (!retained)
                        {
                            allCandidates.Clear(); allCandidates.AddRange(result.candidates);
                            aiRecommendation = forceAi && allCandidates.Count > 0 ? allCandidates[0] : null;
                        }
                        if (!refresh && current == null) resultPlatform = "all";
                        ShowResults(); Sources.Items.Clear();
                        foreach (LookupSource source in result.sources) Sources.Items.Add(new SourceItem(source));
                        ((LookupCandidateList)Candidates).Avatars.Preload(result.candidates, Candidates, cancellation.Token);
                        Status.Text = result.note ?? (result.cached ? "已读取近期缓存。" : "已找到 " + result.candidates.Count + " 个候选；请主动选择并核验。");
                        if (retained) Status.Text += " 左侧保留的是上次候选，本次没有新增结果。";
                        if (aiRecommendation != null) Status.Text += " AI 优先候选固定置顶，不受结果平台筛选限制。";
                        Deep.Enabled = allCandidates.Count > 0;
                        Cancel.Enabled = false;
                        Details.Text = result.cached ? "来源保留原检索时间（" + LookupDiscovery.LocalTime(result.searched_at) + "）；点击刷新结果可绕过搜索缓存。"
                            : "请选择账号查看匹配依据；账号与房间会独立核验。";
                        if (result.platforms.Count > 0)
                            foreach (LookupPlatformOutcome outcome in result.platforms) Details.Text += "\r\n" + Platforms.Name(outcome.platform) + "：" + outcome.count + " 条 · "
                                + (outcome.cached ? "缓存 " : "本次查询 ") + LookupDiscovery.LocalTime(outcome.searched_at) + "\r\n" + (outcome.note ?? "可选择账号继续核验。");
                        SourcePreview.Inner.Text = "选择来源查看摘录；账号详情会保留。";
                        if (forceAi && result.candidates.Count == 0) Details.Text = result.note + (retained ? "\r\n上次候选已保留，可继续核验；本次 AI 没有新增推荐。" : "")
                            + "\r\n本次联网检索来源见下方。建议补充游戏、职业ID或其他称呼，以缩小歧义。";
                    });
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Ui(delegate { if (queries.Current(generation)) { queryCancel = null; Candidates.Enabled = true; Deep.Enabled = allCandidates.Count > 0; UpdateFilters(); Cancel.Enabled = false; Status.Text = ex is LookupFailure ? ex.Message : "查询遇到网络或服务错误，请重试；已有候选保留。"; } }); }
                // Disposing here races the queued UI callback's IsCancellationRequested; source stays small and is reclaimed with its worker.
            });
        }
        internal void BeginBrowserSearch()
        {
            string platform = CurrentPlatform(), text = Input.Text.Trim();
            if (platform != "all" && platform != "douyin") { Status.Text = "抖音网页辅助仅用于抖音账号查询。"; return; }
            if (text.Length == 0 || text.Length > 600) { Status.Text = "请先输入主播昵称或抖音号。"; return; }
            CancelWork(null); string context = platform + "\n" + text;
            if (shownQueryContext != context) { allCandidates.Clear(); aiRecommendation = null; lastResult = null; ShowResults(); Sources.Items.Clear(); }
            shownQueryContext = context; int generation = queries.Next();
            try
            {
                LookupResult result = BrowserSearch(Form, LookupData.DouyinHandleInput(text) ?? LookupDiscovery.SearchTerms(text));
                if (closed || !queries.Current(generation)) return;
                if (result == null) { Status.Text = "已关闭抖音网页辅助；未自动选择或收藏账号。"; return; }
                Service.ClearCache(); Verifier.ClearCache();
                // An empty browser result cannot erase same-query evidence or masquerade as success.
                if (result.candidates.Count > 0)
                {
                    allCandidates.RemoveAll(delegate(LookupCandidate c) { return c.platform == "douyin"; });
                    allCandidates.AddRange(result.candidates); aiRecommendation = null;
                    LookupDiscovery.Rank(allCandidates, text, false);
                }
                lastResult = result; ShowResults(); Sources.Items.Clear();
                foreach (LookupSource source in result.sources) Sources.Items.Add(new SourceItem(source));
                Status.Text = result.note; Deep.Enabled = allCandidates.Count > 0;
                if (result.candidates.Count == 0 && allCandidates.Exists(delegate(LookupCandidate c) { return c.platform == "douyin"; }))
                    Status.Text += " 左侧保留的是同一查询的上次候选，本次网页没有新增结果。";
                Details.Text = "候选保留各自的检索来源与时间；仍需选择后独立核验账号与直播房间，不会自动收藏。";
            }
            catch (Exception ex)
            {
                // Never log browser request URLs, response bodies or credential-bearing errors.
                Status.Text = "抖音网页辅助暂不可用（" + ex.GetType().Name + "）；可使用抖音扫码登录或平台网页搜索。";
            }
        }
        private void SelectCandidate()
        {
            VerifySelectedCandidate(false);
        }
        private void VerifySelectedCandidate(bool refresh)
        {
            if (replacing) return;
            int generation = selections.Next(); if (verifyCancel != null) verifyCancel.Cancel();
            selected = Candidates.SelectedItem as LookupCandidate; Add.Enabled = false; Resolve.Enabled = OpenRoom.Enabled = OpenProfile.Enabled = false;
            if (selected == null) { Details.Text = "请选择候选查看账号与来源。"; return; }
            selected.verification = "unverified"; OpenProfile.Enabled = LookupDiscovery.AccountPage(selected) != null;
            Name.Text = selected.display_name ?? selected.account_handle ?? selected.room_id ?? "主播"; Display(selected);
            LookupCandidate pending = selected;
            var input = LookupData.Copy(selected); var cancellation = new CancellationTokenSource(); verifyCancel = cancellation;
            int query = queries.Next(); if (queryCancel != null) queryCancel.Cancel(); queryCancel = null; Cancel.Enabled = true; Status.Text = "正在核验所选账号与房间归属…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    LookupCandidate result = refresh ? Verifier.VerifyFresh(input, cancellation.Token) : Verifier.Verify(input, cancellation.Token);
                    Ui(delegate
                    {
                        if (!selections.Current(generation) || !queries.Current(query) || cancellation.IsCancellationRequested) return;
                        verifyCancel = null; selected = result; result.user_confirmed = false;
                        int stored = allCandidates.IndexOf(pending); if (stored >= 0) allCandidates[stored] = result;
                        if (ReferenceEquals(pending, aiRecommendation))
                        { aiRecommendation = result; ((LookupCandidateList)Candidates).Recommendation = result; }
                        replacing = true; int index = Candidates.SelectedIndex; if (index >= 0) { Candidates.Items[index] = result; Candidates.SelectedIndex = index; } replacing = false;
                        Name.Text = result.display_name ?? Name.Text; Display(result); UpdateAdd(); Cancel.Enabled = false; Resolve.Enabled = true; Status.Text = result.verification_note;
                    });
                }
                catch (OperationCanceledException) { return; }
                catch (Exception) { Ui(delegate { if (selections.Current(generation) && queries.Current(query)) { verifyCancel = null; Cancel.Enabled = false; Resolve.Enabled = true; Status.Text = "核验受阻，可点击重新解析房间。"; } }); }
            });
        }
        private void Display(LookupCandidate candidate)
        {
            Details.Text = candidate
                + (candidate.platform_update == null ? "" : "\r\n平台去向 · 公开资料提示（需核对公告日期）：" + candidate.platform_update)
                + (candidate.platform == "douyin" ? "\r\n抖音号：" + (candidate.account_handle ?? "尚未取得") : "")
                + "\r\n直播链接：" + (candidate.verification == "verified" ? candidate.room_url : "尚未解析并核验")
                + "\r\n直播状态：" + (candidate.live_status == "live" ? "核验时正在直播" : candidate.live_status == "offline" ? "核验时未开播（房间链接仍可收藏）" : "尚未确定") + "；当前状态以启动器为准。"
                + "\r\n粉丝量：" + (candidate.follower_count == null ? "平台未提供" : LookupDiscovery.FollowerDetail(candidate.follower_count))
                + (candidate.platform == "douyin" ? "\r\n账号资料：" + (candidate.account_verified ? "已核实平台身份" : "待核实")
                    + "\r\n解析路径：" + DouyinResolution(candidate.resolution_path) : "")
                + "\r\n内容分类：" + (candidate.category ?? "平台未提供")
                + "\r\n账号简介：" + (candidate.description ?? "平台未提供")
                + "\r\n直播标题：" + (candidate.live_title ?? "平台未提供")
                + "\r\n身份匹配：" + LookupDiscovery.RelevanceName(candidate.relevance)
                + (candidate.relevance_note == null ? "" : "\r\n公开匹配意见（需对照来源）：" + candidate.relevance_note)
                + "\r\n匹配依据：" + string.Join("；", candidate.match_reasons ?? new string[0])
                + "\r\n" + (candidate.verification_note ?? "匹配依据来自检索材料，正在独立核验。")
                + "\r\n核验时间：" + (candidate.verified_at ?? "尚未核验")
                + "\r\n候选来源：" + (candidate.provider == "public_index" ? "公开索引" : candidate.provider == "deepseek_official" ? "DeepSeek 原生联网研究" : "平台查询")
                + ((candidate.source ?? "").Contains("+ai_context_review") ? "（已手动补充 AI 线索）" : "")
                + "\r\n账号 ID：" + (candidate.account_id ?? "尚未取得")
                + (candidate.platform == "douyin" ? "\r\n主页标识 sec_uid：" + (candidate.sec_uid ?? "尚未取得") : "");
            Sources.Items.Clear(); foreach (LookupSource source in candidate.sources) Sources.Items.Add(new SourceItem(source)); Sources.SelectedIndex = -1;
            SourcePreview.Inner.Text = "选择来源查看摘录；匹配意见和房间归属核验分别展示。"; OpenProfile.Enabled = LookupDiscovery.AccountPage(candidate) != null;
            ((LookupCandidateList)Candidates).Avatars.Preload(new[] { candidate }, Candidates, CancellationToken.None);
        }
        private static string DouyinResolution(string path)
        {
            if (path == "profile_api_then_alias_ssr_then_reflow") return "账号资料 → 抖音号 → 直播页 → 数字房间核验";
            if (path == "handle_hint_then_alias_ssr_then_reflow") return "抖音号线索 → 同账号直播页 → 数字房间核验";
            if (path == "profile_api_then_reflow") return "账号资料 → 场次 → 数字房间核验";
            if (path == "profile_ssr_then_reflow") return "账号主页 → 场次 → 数字房间核验";
            return path == "room_ssr_then_reflow" ? "直播页 → 场次 → 数字房间核验" : "等待平台返回账号与直播关联";
        }
        private void OpenPlatformSearch()
        {
            string platform = CurrentPlatform();
            if (platform != "all") { OpenPublicUrl(LookupUrls.SearchPage(platform, Input.Text.Trim())); return; }
            var menu = new ContextMenuStrip();
            foreach (string key in LookupDiscovery.PlatformKeys)
            {
                string actual = key; menu.Items.Add(Platforms.Name(actual), null, delegate { OpenPublicUrl(LookupUrls.SearchPage(actual, Input.Text.Trim())); });
            }
            menu.Closed += delegate { menu.Dispose(); }; menu.Show(Web, new Point(0, Web.Height));
        }
        private void ShowClues()
        {
            using (var dialog = new DpiForm { Text = "补充主播线索", Font = ModernTheme.UiFont, StartPosition = FormStartPosition.CenterParent,
                BackColor = ModernTheme.Surface, ForeColor = ModernTheme.Text, ClientSize = new Size(DpiLayout.S(500), DpiLayout.S(220)), AutoScaleMode = AutoScaleMode.None })
            {
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(DpiLayout.S(12)), ColumnCount = 2, RowCount = 4 };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, DpiLayout.S(80))); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                var fields = new ModernInput[3]; string[] labels = { "游戏/领域", "战队/身份", "外号/曾用名" };
                for (int i = 0; i < fields.Length; i++) {
                    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, DpiLayout.S(42)));
                    layout.Controls.Add(new ModernLabel { Text = labels[i], Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, i);
                    fields[i] = new ModernInput(false) { Dock = DockStyle.Fill }; layout.Controls.Add(fields[i], 1, i);
                }
                var apply = new ModernButton { Text = "应用线索", Dock = DockStyle.Right, Width = DpiLayout.S(100) };
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.Controls.Add(apply, 1, 3); dialog.Controls.Add(layout); dialog.AcceptButton = apply;
                apply.Click += delegate {
                    string text = Input.Text.Trim();
                    for (int i = 0; i < fields.Length; i++) if (!string.IsNullOrWhiteSpace(fields[i].Text)) text += " " + labels[i] + "：" + fields[i].Text.Trim();
                    if (text.Length > 600) { MessageBox.Show(dialog, "线索总长度不能超过600字。"); return; }
                    Input.Text = text; dialog.Close(); Status.Text = "线索已补充，可点击查询或 AI 重新搜索。";
                };
                dialog.HandleCreated += delegate { ModernTheme.ApplyWindowChrome(dialog); }; dialog.ShowDialog(Form);
            }
        }
        private void UpdateAdd()
        {
            Add.Enabled = verifyCancel == null && LookupData.VerifiedRoomKey(selected) != null && !string.IsNullOrWhiteSpace(Name.Text);
            OpenRoom.Enabled = verifyCancel == null && selected != null && selected.verification == "verified" && LookupData.Digits(selected.room_id);
        }
        private void OpenSource()
        {
            var item = Sources.SelectedItem as SourceItem;
            if (item == null) { Status.Text = "请先在来源列表选择一条来源。"; return; }
            OpenPublicUrl(item.Source.url);
        }
        private void CloseLookupWork()
        {
            if (closed) return;
            closed = true; queries.Next(); selections.Next();
            if (queryCancel != null) queryCancel.Cancel();
            if (verifyCancel != null) verifyCancel.Cancel();
            queryCancel = null; verifyCancel = null;
        }
        public void Dispose() { CloseLookupWork(); ModernTheme.Changed -= FollowTheme; if (ownsForm) Form.Dispose(); else View.Dispose(); }
    }
    internal sealed class SourceItem
    {
        internal readonly LookupSource Source;
        internal SourceItem(LookupSource source) { Source = source; }
        public override string ToString() { return "[" + (Source.index ?? "平台") + "] " + Source.title + " · " + Source.retrieved_at; }
    }
    private static void OpenPublicUrl(string url)
    {
        if (!LookupUrls.PublicSource(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { MessageBox.Show("无法打开浏览器，请从来源详情复制链接。", "看直播"); }
    }
    private static void ShowDeepSeekSettings(Form owner)
    {
        DeepSeekLookupConfig config;
        try { config = DeepSeekLookupConfig.Load(); } catch (LookupFailure) { config = new DeepSeekLookupConfig { model = "deepseek-flash" }; }
        using (var dialog = new DpiForm { Text = "DeepSeek 设置 · 本机加密凭据", StartPosition = FormStartPosition.CenterParent,
            Font = ModernTheme.UiFont, BackColor = ModernTheme.Surface, ForeColor = ModernTheme.Text,
            ClientSize = new Size(DpiLayout.S(680), DpiLayout.S(280)), MinimumSize = new Size(DpiLayout.S(620), DpiLayout.S(300)), AutoScaleMode = AutoScaleMode.None })
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(DpiLayout.S(12)), ColumnCount = 2, RowCount = 4 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, DpiLayout.S(100))); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var model = new ModernInput(false) { Text = config.model, Dock = DockStyle.Fill };
            var key = new ModernInput(false) { Dock = DockStyle.Fill }; key.Inner.UseSystemPasswordChar = true;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, DpiLayout.S(42))); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, DpiLayout.S(42)));
            layout.Controls.Add(new ModernLabel { Text = "文本模型", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0); layout.Controls.Add(model, 1, 0);
            layout.Controls.Add(new ModernLabel { Text = "API Key", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1); layout.Controls.Add(key, 1, 1);
            var note = new ModernLabel { Dock = DockStyle.Fill, Role = ModernLabel.RoleKind.Secondary,
                Text = "DeepSeek 官方原生联网搜索；密钥留空保留现有凭据。\r\n普通查询不调用 AI；AI 优化与重新搜索仅手动执行，可取消。\r\n凭据只保存在当前 Windows 用户本机，换机需重新配置。" };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.Controls.Add(note, 0, 2); layout.SetColumnSpan(note, 2);
            var save = new ModernButton { Text = "保存", Height = DpiLayout.S(34), Dock = DockStyle.Right };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, DpiLayout.S(44))); layout.Controls.Add(save, 1, 3); dialog.Controls.Add(layout); dialog.AcceptButton = save;
            Action followTheme = delegate { dialog.BackColor = ModernTheme.Surface; dialog.ForeColor = ModernTheme.Text; ModernTheme.ApplyWindowChrome(dialog); };
            dialog.HandleCreated += delegate { ModernTheme.ApplyWindowChrome(dialog); };
            ModernTheme.Changed += followTheme; dialog.Disposed += delegate { ModernTheme.Changed -= followTheme; };
            save.Click += delegate
            {
                try { DeepSeekLookupConfig.Save(new DeepSeekLookupConfig { model = model.Text.Trim() }, key.Text); key.Text = ""; dialog.Close(); }
                catch (Exception ex) { note.Text = ex is LookupFailure ? ex.Message : "本机配置未保存，请检查权限。"; }
            };
            dialog.ShowDialog(owner);
        }
    }
}
