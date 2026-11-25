// =====================================================================
//  Mouse Mapper  -  geef de knoppen van je muis eigen functies
//  Terug-knop, vooruit-knop en middelste knop: één klik, dubbelklik en
//  vasthouden. Werkt met elke muis, zonder de software van de fabrikant.
//  Gedeelde onderdelen staan in Common.cs, het ronde menu in Radial.cs.
// =====================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Mouse Mapper")]
[assembly: AssemblyProduct("Mouse Mapper")]
[assembly: AssemblyDescription("Mouse Mapper")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace PenMapper
{
    static class AppInfo
    {
        public const string Name = "Mouse Mapper";
        public const string Id = "MouseMapper";
        public const string Version = "1.0";
        public static readonly string Glyph = "" + (char)0xE962;
        public static string Tagline { get { return L.T("muisknoppen naar wens", "your mouse, your way"); } }
    }

    class MainForm : Form
    {
        static readonly string[] GestureKeys = { "enkel", "dubbel", "houden" };
        static readonly int[] DblValues = { 250, 350, 500 };
        static readonly int[] HoldValues = { 400, 600, 900 };
        const int W = 920, H = 740, SB = 220, PX = 28;
        const int CW = W - SB - 2 * PX;
        const int CardH = 190;

        // "Standaard" = de knop doet wat hij normaal doet
        static readonly ActionDef NativeAction = new ActionDef("native", "", L.T("Standaard (normale knop)", "Default (normal button)"), ActionKind.Special, null);

        readonly PenButton[] buttons = new PenButton[3];
        readonly Dictionary<string, Panel> pages = new Dictionary<string, Panel>();
        readonly Dictionary<string, NavItem> navs = new Dictionary<string, NavItem>();
        readonly OsdForm osd = new OsdForm();
        NotifyIcon tray;
        ContextMenuStrip trayMenu, quickMenu;
        ToolStripMenuItem trayToggle;
        Label mapDot, mapLabel, liveLabel;
        ToggleSwitch tglMapping, tglAutostart, tglOsd;
        DarkCombo cmbDbl, cmbHold;
        bool mappingOn, osdOn, exiting, toldTray, loading;
        RadialMenu radial;
        RadialConfig radialCfg;
        PenButton radialHoldButton;
        IntPtr hook = IntPtr.Zero;
        HookProc hookProc;

        public MainForm(string startPage, EventWaitHandle showEvent, EventWaitHandle exitEvent)
        {
            mappingOn = Settings.GetBool("mapping", true);
            osdOn = Settings.GetBool("melding", true);

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = AppInfo.Name;
            ClientSize = new Size(W, H);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Regular(10);
            DoubleBuffered = true;
            var icon = LoadAppIcon(32);
            if (icon != null) Icon = icon;

            buttons[0] = new PenButton { Key = "terug", Title = L.T("Terug-knop", "Back button"), Hint = L.T("zijknop, meestal de achterste", "side button, usually the rear one") };
            buttons[1] = new PenButton { Key = "vooruit", Title = L.T("Vooruit-knop", "Forward button"), Hint = L.T("zijknop, meestal de voorste", "side button, usually the front one") };
            buttons[2] = new PenButton { Key = "midden", Title = L.T("Middelste knop", "Middle button"), Hint = L.T("klik op het scrollwiel", "click the scroll wheel") };
            foreach (var b in buttons)
            {
                PenButton bb = b;
                b.HoldTimer.Tick += (s, e) => OnHold(bb);
                b.SingleTimer.Tick += (s, e) => OnSingle(bb);
            }

            radialCfg = new RadialConfig();
            radialCfg.Load();
            radial = new RadialMenu();
            radial.Picked += it => RunAction(it.Action, it.Param, null);

            BuildSidebar();
            BuildButtonsPage();
            BuildRadialPage();
            BuildSettingsPage();
            BuildHelpPage();
            BuildMenus();
            LoadSlots();

            ResumeLayout(false);
            PerformAutoScale();

            SetMapping(mappingOn, false);
            foreach (var b in buttons) SetLed(b);
            ShowPage(startPage != null && pages.ContainsKey(startPage) ? startPage : "knoppen");

            InstallHook();
            StartWaiter(showEvent, ShowMain);
            StartWaiter(exitEvent, ExitApp);
        }

        void StartWaiter(EventWaitHandle ev, Action action)
        {
            var t = new Thread(() =>
            {
                while (true)
                {
                    ev.WaitOne();
                    try { BeginInvoke(action); } catch { }
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        // ----------------------------------------------------- venster
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int dark = 1;
            Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!toldTray)
                {
                    toldTray = true;
                    tray.ShowBalloonTip(3000, AppInfo.Name,
                        L.T("Mouse Mapper blijft actief. Dubbelklik het muis-icoon rechtsonder om het venster te openen.",
                            "Mouse Mapper keeps running. Double-click the mouse icon in the system tray to open it."), ToolTipIcon.None);
                }
                return;
            }
            base.OnFormClosing(e);
        }

        void ShowMain()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        void ExitApp()
        {
            exiting = true;
            if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            Application.Exit();
        }

        public static Icon LoadAppIcon(int size)
        {
            try
            {
                var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("PenMapper.app.ico");
                if (s == null) return null;
                return new Icon(s, size, size);
            }
            catch { return null; }
        }

        // ----------------------------------------------------- hulpjes
        static Label AddLabel(Control parent, string text, Font font, Color fg, int x, int y, int w, int h,
            ContentAlignment align = ContentAlignment.MiddleLeft)
        {
            var rp = parent as RoundPanel;
            var l = new Label
            {
                Text = text, Font = font, ForeColor = fg,
                BackColor = rp != null ? rp.Fill : parent.BackColor,
                Location = new Point(x, y), Size = new Size(w, h),
                TextAlign = align, AutoSize = false, UseMnemonic = false
            };
            parent.Controls.Add(l);
            return l;
        }

        static RoundPanel AddCard(Control parent, int x, int y, int w, int h)
        {
            var c = new RoundPanel { Fill = Theme.Card, BackColor = parent.BackColor, Location = new Point(x, y), Size = new Size(w, h) };
            parent.Controls.Add(c);
            return c;
        }

        Panel NewPage(string name, string title, string sub)
        {
            var p = new Panel { Location = new Point(SB, 0), Size = new Size(W - SB, H), BackColor = Theme.Bg, Visible = false };
            Controls.Add(p);
            pages[name] = p;
            AddLabel(p, title, Theme.Bold(19), Theme.Text, PX, 22, CW, 42);
            AddLabel(p, sub, Theme.Regular(10), Theme.Muted, PX, 64, CW, 22);
            return p;
        }

        FlatButton AddButton(Control parent, string text, int x, int y, int w, int h, bool accent, EventHandler click)
        {
            var b = new FlatButton
            {
                Text = text, Location = new Point(x, y), Size = new Size(w, h),
                Fill = accent ? Theme.Accent : Theme.Field, HoverFill = accent ? Theme.AccentHover : Theme.FieldHover,
                ForeColor = accent ? Color.White : Theme.Text
            };
            b.Click += click;
            parent.Controls.Add(b);
            return b;
        }

        // ----------------------------------------------------- zijbalk
        void BuildSidebar()
        {
            var side = new SidebarPanel { Location = new Point(0, 0), Size = new Size(SB, H), BackColor = Theme.Side };
            Controls.Add(side);
            AddNav(side, "knoppen", "" + (char)0xE962, L.T("Knoppen", "Buttons"), 104);
            AddNav(side, "radial", "" + (char)0xE8A9, "Radial menu", 150);
            AddNav(side, "instellingen", "" + (char)0xE713, L.T("Instellingen", "Settings"), 196);
            AddNav(side, "hulp", "" + (char)0xE946, L.T("Hulp en test", "Help and test"), 242);
            mapDot = AddLabel(side, "●", Theme.Regular(9), Theme.Good, 22, H - 48, 18, 22);
            mapLabel = AddLabel(side, "-", Theme.Regular(9), Theme.Muted, 42, H - 48, 170, 22);
        }

        void AddNav(Control parent, string page, string glyph, string text, int y)
        {
            var n = new NavItem { Glyph = glyph, Text = text, Location = new Point(12, y), Size = new Size(SB - 24, 40) };
            n.Click += (s, e) => ShowPage(page);
            parent.Controls.Add(n);
            navs[page] = n;
        }

        void ShowPage(string name)
        {
            foreach (var kv in pages) kv.Value.Visible = kv.Key == name;
            foreach (var kv in navs) kv.Value.Selected = kv.Key == name;
        }

        // ------------------------------------------------- pagina: knoppen
        void BuildButtonsPage()
        {
            var p = NewPage("knoppen", L.T("Knoppen", "Buttons"),
                L.T("Kies per muisknop wat één klik, een dubbelklik en vasthouden doen.",
                    "Choose what a single click, a double click and holding do for each mouse button."));
            for (int i = 0; i < 3; i++) BuildButtonCard(p, buttons[i], 100 + i * (CardH + 12));
            AddLabel(p, L.T("Tip: 'Standaard' laat de knop gewoon doen wat hij altijd deed, zoals terug in je browser.",
                            "Tip: 'Default' keeps the button doing what it always did, like going back in your browser."),
                Theme.Regular(9), Theme.Muted, PX, 100 + 3 * (CardH + 12), CW, 22);
        }

        void BuildButtonCard(Panel page, PenButton b, int y)
        {
            string[] gestureNames = { L.T("Eén klik", "Single click"), L.T("Dubbelklik", "Double click"), L.T("Vasthouden", "Hold") };
            var card = AddCard(page, PX, y, CW, CardH);
            b.TitleLabel = AddLabel(card, b.Title, Theme.Semi(12), Theme.Text, 22, 12, 260, 28);
            b.SubLabel = AddLabel(card, b.Hint, Theme.Regular(9), Theme.Muted, 22, 40, 400, 20);
            b.Led = AddLabel(card, "-", Theme.Semi(10), Theme.Muted, CW - 22 - 160, 14, 160, 26, ContentAlignment.MiddleRight);

            for (int i = 0; i < 3; i++)
            {
                int ry = 68 + i * 40;
                AddLabel(card, gestureNames[i], Theme.Regular(10), Theme.Muted, 22, ry, 104, 32);
                var slot = new Slot { Gesture = GestureKeys[i], Owner = b };
                slot.Combo = new DarkCombo { Location = new Point(130, ry + 1), Width = 300 };
                slot.Combo.Items.Add(NativeAction);
                foreach (var a in Actions.All) slot.Combo.Items.Add(a);
                card.Controls.Add(slot.Combo);
                slot.Field = new FieldBox { Location = new Point(442, ry), Size = new Size(CW - 442 - 22 - 40, 32), Visible = false };
                card.Controls.Add(slot.Field);
                Slot sl = slot;
                slot.Browse = AddButton(card, "...", CW - 22 - 34, ry, 34, 32, false, (s, e) => BrowseProgram(sl));
                slot.Browse.Visible = false;
                slot.Combo.SelectedIndexChanged += (s, e) =>
                {
                    if (!loading) { sl.Field.Box.Combo = ""; sl.Field.Box.Text = ""; }
                    UpdateParamUI(sl);
                    SaveSlot(sl);
                };
                slot.Field.Box.TextChanged += (s, e) => SaveSlot(sl);
                b.Slots[i] = slot;
            }
        }

        void UpdateParamUI(Slot s)
        {
            var a = s.Action;
            bool needs = a != null && a.NeedsParam;
            s.Field.Visible = needs;
            s.Browse.Visible = a != null && a.Kind == ActionKind.Run;
            s.Field.Width = s.Browse.Visible ? CW - 442 - 22 - 40 : CW - 442 - 22;
            if (!needs) return;
            s.Field.Box.ShortcutMode = a.Kind == ActionKind.Shortcut;
            s.Field.Box.Cue = a.Kind == ActionKind.Shortcut ? L.T("klik en druk een sneltoets", "click and press a shortcut")
                : a.Kind == ActionKind.Text ? L.T("tekst om te typen", "text to type") : L.T("programma of https://...", "program or https://...");
        }

        static ActionDef ById(string id)
        {
            if (id == "native") return NativeAction;
            return Actions.ById(id);
        }

        void LoadSlots()
        {
            loading = true;
            foreach (var b in buttons)
                foreach (var s in b.Slots)
                {
                    string def = s.Gesture == "enkel" ? "native"
                        : (b.Key == "terug" && s.Gesture == "houden") ? "radial" : "none";
                    var a = ById(Settings.Get(b.Key + "." + s.Gesture, def)) ?? Actions.All[0];
                    s.Combo.SelectedItem = a;
                    string param = Settings.Get(b.Key + "." + s.Gesture + ".waarde", "");
                    UpdateParamUI(s);
                    if (a.Kind == ActionKind.Shortcut) s.Field.Box.SetCombo(param);
                    else s.Field.Box.Text = param;
                }
            loading = false;
        }

        void SaveSlot(Slot s)
        {
            if (loading) return;
            var a = s.Action;
            string k = s.Owner.Key + "." + s.Gesture;
            Settings.Set(k, a != null ? a.Id : "none");
            Settings.Set(k + ".waarde", a != null && a.NeedsParam ? s.Param : "");
        }

        void BrowseProgram(Slot s)
        {
            using (var dlg = new OpenFileDialog { Title = L.T("Kies een programma", "Choose a program"), Filter = L.T("Programma's", "Programs") + "|*.exe;*.lnk;*.bat;*.cmd|" + L.T("Alle bestanden", "All files") + "|*.*" })
                if (dlg.ShowDialog(this) == DialogResult.OK) s.Field.Box.Text = dlg.FileName;
        }

        // ---------------------------------------------------- pagina: radial menu
        void BuildRadialPage()
        {
            var p = NewPage("radial", "Radial menu",
                L.T("Kies wat er in de 8 vakken van het ronde menu staat. Open het met de actie 'Radial menu openen'.",
                    "Choose what goes in the 8 slices of the round menu. Open it with the 'Open radial menu' action."));
            radialCfg.BuildEditor(p, PX, 100, CW);
            AddButton(p, L.T("Radial menu testen", "Test radial menu"), PX, 100 + 16 + RadialConfig.Count * 46 + 16, 200, 38, true,
                (s, e) => radial.ShowAt(Cursor.Position, radialCfg.Items, false));
        }

        // ------------------------------------------------ pagina: instellingen
        void BuildSettingsPage()
        {
            var p = NewPage("instellingen", L.T("Instellingen", "Settings"), L.T("Hoe Mouse Mapper werkt en reageert.", "How Mouse Mapper behaves."));
            const int rows = 6;
            var card = AddCard(p, PX, 100, CW, rows * 64 + 16);
            tglMapping = SettingToggle(card, 0, L.T("Mapping aan", "Mapping on"), L.T("Uit = alle muisknoppen werken weer normaal.", "Off = all mouse buttons work normally again."), mappingOn);
            tglMapping.CheckedChanged += (s, e) => SetMapping(tglMapping.Checked, true);
            tglAutostart = SettingToggle(card, 1, L.T("Start met Windows", "Start with Windows"),
                L.T("Mouse Mapper start vanzelf op de achtergrond.", "Mouse Mapper starts automatically in the background."), Autostart.IsOn);
            tglAutostart.CheckedChanged += (s, e) => Autostart.Set(tglAutostart.Checked);
            tglOsd = SettingToggle(card, 2, L.T("Melding bij actie", "Show action label"),
                L.T("Toon een klein label bij de cursor als een actie wordt uitgevoerd.", "Show a small label near the cursor when an action runs."), osdOn);
            tglOsd.CheckedChanged += (s, e) => { osdOn = tglOsd.Checked; Settings.SetBool("melding", osdOn); };
            cmbDbl = SettingCombo(card, 3, L.T("Dubbelklik-snelheid", "Double-click speed"), L.T("Hoe snel je twee keer moet drukken.", "How fast you need to press twice."),
                new[] { L.T("Snel (250 ms)", "Fast (250 ms)"), L.T("Normaal (350 ms)", "Normal (350 ms)"), L.T("Langzaam (500 ms)", "Slow (500 ms)") }, "dubbelklik", 1);
            cmbHold = SettingCombo(card, 4, L.T("Vasthouden na", "Hold after"), L.T("Hoe lang je de knop ingedrukt houdt.", "How long you keep the button pressed."),
                new[] { L.T("Kort (400 ms)", "Short (400 ms)"), L.T("Normaal (600 ms)", "Normal (600 ms)"), L.T("Lang (900 ms)", "Long (900 ms)") }, "vasthouden", 1);
            string[] langIds = { "auto", "nl", "en" };
            var cmbLang = SettingCombo(card, 5, L.T("Taal", "Language"), L.T("Mouse Mapper start opnieuw als je dit wijzigt.", "Mouse Mapper restarts when you change this."),
                new[] { L.T("Automatisch (Windows)", "Automatic (Windows)"), "Nederlands", "English" }, null, 0);
            int li = Array.IndexOf(langIds, Settings.Get("taal", "en"));
            cmbLang.SelectedIndex = li < 0 ? 0 : li;
            cmbLang.SelectedIndexChanged += (s, e) =>
            {
                Settings.Set("taal", langIds[cmbLang.SelectedIndex]);
                try { Process.Start(Application.ExecutablePath, "/restart /page:instellingen"); } catch { }
                ExitApp();
            };
            AddButton(p, L.T("Snelmenu openen", "Open quick menu"), PX, 100 + rows * 64 + 16 + 16, 200, 38, false, (s, e) => quickMenu.Show(Cursor.Position));
        }

        void SettingTexts(RoundPanel card, int i, string title, string desc)
        {
            int y = 8 + i * 64;
            if (i > 0) card.Controls.Add(new Panel { BackColor = Theme.Line, Location = new Point(22, y), Size = new Size(CW - 44, 1) });
            AddLabel(card, title, Theme.Semi(10), Theme.Text, 22, y + 10, 400, 22);
            AddLabel(card, desc, Theme.Regular(9), Theme.Muted, 22, y + 32, 420, 20);
        }

        ToggleSwitch SettingToggle(RoundPanel card, int i, string title, string desc, bool state)
        {
            SettingTexts(card, i, title, desc);
            var t = new ToggleSwitch { Checked = state, Location = new Point(CW - 22 - 48, 8 + i * 64 + 19), Size = new Size(48, 26), BackColor = Theme.Card };
            card.Controls.Add(t);
            return t;
        }

        DarkCombo SettingCombo(RoundPanel card, int i, string title, string desc, string[] items, string key, int def)
        {
            SettingTexts(card, i, title, desc);
            var c = new DarkCombo { Location = new Point(CW - 22 - 210, 8 + i * 64 + 17), Width = 210 };
            c.Items.AddRange(items);
            if (key != null)
            {
                int v = Settings.GetInt(key, def);
                c.SelectedIndex = (v >= 0 && v < items.Length) ? v : def;
                c.SelectedIndexChanged += (s, e) => Settings.Set(key, c.SelectedIndex.ToString());
            }
            card.Controls.Add(c);
            return c;
        }

        // ---------------------------------------------------- pagina: hulp
        void BuildHelpPage()
        {
            var p = NewPage("hulp", L.T("Hulp en test", "Help and test"),
                L.T("Controleer of je muisknoppen gezien worden en lees hoe alles werkt.", "Check that your mouse buttons are detected and learn how everything works."));
            var live = AddCard(p, PX, 100, CW, 120);
            AddLabel(live, L.T("Live test", "Live test"), Theme.Semi(11), Theme.Text, 22, 14, 300, 26);
            liveLabel = AddLabel(live, L.T("Druk op een zijknop of de middelste knop...", "Press a side button or the middle button..."), Theme.Semi(11), Theme.Accent, 22, 46, CW - 44, 28);
            AddLabel(live, L.T("Werkt een knop niet? Sommige muizen sturen hun zijknoppen als toetsen; die worden niet herkend.", "A button not working? Some mice send their side buttons as keys; those are not detected."),
                Theme.Regular(9), Theme.Muted, 22, 80, CW - 44, 22);
            var how = AddCard(p, PX, 236, CW, 300);
            AddLabel(how, L.T("Zo gebruik je het", "How to use it"), Theme.Semi(11), Theme.Text, 22, 14, 300, 26);
            AddLabel(how, L.T(
                "•  Per knop stel je drie dingen in: één klik, twee keer snel drukken, of ingedrukt houden.\n\n" +
                "•  'Standaard' laat de knop doen wat hij normaal doet. Staan alle drie op Standaard, dan blijft de knop helemaal normaal.\n\n" +
                "•  Heeft een knop een dubbelklik-actie, dan wacht 'één klik' heel even of er nog een klik komt.\n\n" +
                "•  'Radial menu openen' toont een rond menu met 8 acties; 'Snelmenu openen' een lijst met alle acties.\n\n" +
                "•  Sluit je dit venster, dan blijft Mouse Mapper actief rechtsonder in het systeemvak.",
                "•  Each button has three settings: a single click, pressing twice quickly, or holding it.\n\n" +
                "•  'Default' keeps the button's normal function. If all three are Default, the button stays completely normal.\n\n" +
                "•  If a button has a double-click action, a single click waits a moment for a second click.\n\n" +
                "•  'Open radial menu' shows a round menu with 8 actions; 'Open quick menu' a list of all actions.\n\n" +
                "•  Closing this window keeps Mouse Mapper running in the system tray."),
                Theme.Regular(10), Theme.Muted, 22, 48, CW - 44, 240, ContentAlignment.TopLeft);
            AddLabel(p, AppInfo.Name + " " + AppInfo.Version + "   ·   " + L.T("werkt met elke muis met zijknoppen", "works with any mouse with side buttons"),
                Theme.Regular(9), Theme.Muted, PX, 560, CW, 22);
        }

        // ------------------------------------------------------- menu's
        void BuildMenus()
        {
            trayMenu = new ContextMenuStrip { Renderer = new DarkRenderer(), Font = Theme.Regular(10), ShowImageMargin = false, ShowCheckMargin = true };
            trayMenu.Items.Add(L.T("Mouse Mapper openen", "Open Mouse Mapper"), null, (s, e) => ShowMain());
            trayMenu.Items.Add(L.T("Snelmenu", "Quick menu"), null, (s, e) => quickMenu.Show(Cursor.Position));
            trayToggle = new ToolStripMenuItem(L.T("Mapping aan", "Mapping on"), null, (s, e) => SetMapping(!mappingOn, true));
            trayMenu.Items.Add(trayToggle);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(L.T("Afsluiten", "Exit"), null, (s, e) => ExitApp());
            foreach (ToolStripItem it in trayMenu.Items) it.ForeColor = Theme.Text;
            tray = new NotifyIcon { Icon = LoadAppIcon(16) ?? SystemIcons.Application, Text = AppInfo.Name, ContextMenuStrip = trayMenu, Visible = true };
            tray.DoubleClick += (s, e) => ShowMain();

            quickMenu = new ContextMenuStrip { Renderer = new DarkRenderer(), Font = Theme.Regular(10), ShowImageMargin = false };
            var cats = new Dictionary<string, ToolStripMenuItem>();
            foreach (var a in Actions.All)
            {
                if (a.Kind == ActionKind.None || a.NeedsParam || a.Id == "menu") continue;
                ToolStripMenuItem cat;
                if (!cats.TryGetValue(a.Category, out cat))
                {
                    cat = new ToolStripMenuItem(a.Category) { ForeColor = Theme.Text };
                    ((ToolStripDropDownMenu)cat.DropDown).ShowImageMargin = false;
                    cat.DropDown.Renderer = new DarkRenderer();
                    cats[a.Category] = cat;
                    quickMenu.Items.Add(cat);
                }
                ActionDef act = a;
                cat.DropDownItems.Add(new ToolStripMenuItem(a.Name, null, (s, e) => RunAction(act, "", null)) { ForeColor = Theme.Text });
            }
        }

        void SetMapping(bool on, bool save)
        {
            mappingOn = on;
            if (tglMapping != null && tglMapping.Checked != on) tglMapping.Checked = on;
            mapDot.ForeColor = on ? Theme.Good : Theme.Off;
            mapLabel.Text = on ? L.T("Mapping actief", "Mapping active") : L.T("Gepauzeerd", "Paused");
            if (trayToggle != null) trayToggle.Checked = on;
            if (save) Settings.SetBool("mapping", on);
        }

        void SetLed(PenButton b)
        {
            b.Led.Text = b.Down ? "●  " + L.T("ingedrukt", "pressed") : "○  " + L.T("los", "released");
            b.Led.ForeColor = b.Down ? Theme.Accent : Theme.Muted;
        }

        // ------------------------------------------------ muis-hook
        delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public IntPtr extraInfo; }

        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

        void InstallHook()
        {
            hookProc = OnMouseHook;   // vasthouden, anders ruimt de garbage collector hem op
            hook = SetWindowsHookEx(14, hookProc, GetModuleHandle(null), 0);   // WH_MOUSE_LL
        }

        bool IsActive(PenButton b)
        {
            foreach (var s in b.Slots)
            {
                var a = s.Action;
                if (a != null && a.Id != "native") return true;
            }
            return false;
        }

        IntPtr OnMouseHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = (int)wParam;
                var info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                if ((info.flags & 1) == 0)   // niet door software verzonden
                {
                    PenButton b = null;
                    bool down = false;
                    if (msg == 0x020B || msg == 0x020C)
                    {
                        int x = (int)((info.mouseData >> 16) & 0xFFFF);
                        b = x == 1 ? buttons[0] : x == 2 ? buttons[1] : null;
                        down = msg == 0x020B;
                    }
                    else if (msg == 0x0207 || msg == 0x0208)
                    {
                        b = buttons[2];
                        down = msg == 0x0207;
                    }
                    if (b != null)
                    {
                        bool handle = mappingOn && IsActive(b);
                        if (down != b.Down)
                        {
                            b.Down = down;
                            SetLed(b);
                            liveLabel.Text = b.Title + (down ? L.T("  ·  ingedrukt", "  ·  pressed") : L.T("  ·  losgelaten", "  ·  released"));
                            if (handle) { if (down) OnPress(b); else OnRelease(b); }
                        }
                        if (handle) return (IntPtr)1;   // origineel tegenhouden
                    }
                }
            }
            return CallNextHookEx(hook, nCode, wParam, lParam);
        }

        // ----------------------------------------------------- gebaren
        bool HasAction(PenButton b, int g)
        {
            var a = b.Slots[g].Action;
            return a != null && a.Kind != ActionKind.None;
        }

        void OnPress(PenButton b)
        {
            b.Held = false;
            if (HasAction(b, 2))
            {
                b.HoldTimer.Interval = HoldValues[Math.Max(0, cmbHold.SelectedIndex)];
                b.HoldTimer.Start();
            }
        }

        void OnRelease(PenButton b)
        {
            b.HoldTimer.Stop();
            if (b.Held)
            {
                if (radialHoldButton == b && radial.IsOpen) { radialHoldButton = null; radial.CommitHover(); }
                return;
            }
            if (HasAction(b, 1))
            {
                if (b.Pending) { b.Pending = false; b.SingleTimer.Stop(); Fire(b, 1); }
                else
                {
                    b.Pending = true;
                    b.SingleTimer.Interval = DblValues[Math.Max(0, cmbDbl.SelectedIndex)];
                    b.SingleTimer.Start();
                }
            }
            else Fire(b, 0);
        }

        void OnHold(PenButton b)
        {
            b.HoldTimer.Stop();
            b.Held = true;
            b.Pending = false;
            b.SingleTimer.Stop();
            Fire(b, 2);
        }

        void OnSingle(PenButton b)
        {
            b.SingleTimer.Stop();
            b.Pending = false;
            Fire(b, 0);
        }

        void Fire(PenButton b, int g)
        {
            var slot = b.Slots[g];
            var a = slot.Action;
            if (a == null || a.Kind == ActionKind.None) return;
            if (!mappingOn && a.Id != "pause") return;
            string param = slot.Param;
            if (a.Id == "radial") radialHoldButton = g == 2 ? b : null;
            BeginInvoke(new Action(() => RunAction(a, param, b)));
        }

        // De originele muisknop opnieuw versturen (voor 'Standaard')
        static void Replay(PenButton b)
        {
            uint down, up, data = 0;
            if (b.Key == "midden") { down = 0x0020; up = 0x0040; }
            else { down = 0x0080; up = 0x0100; data = b.Key == "terug" ? 1u : 2u; }
            var arr = new Native.INPUT[2];
            arr[0].type = 0; arr[0].U.mi.dwFlags = down; arr[0].U.mi.mouseData = data;
            arr[1].type = 0; arr[1].U.mi.dwFlags = up; arr[1].U.mi.mouseData = data;
            Native.SendInput(2, arr, Marshal.SizeOf(typeof(Native.INPUT)));
        }

        // ------------------------------------------------- acties uitvoeren
        void RunAction(ActionDef a, string param, PenButton source)
        {
            try
            {
                if (a.Id == "native")
                {
                    if (source != null) Replay(source);
                    return;
                }
                switch (a.Kind)
                {
                    case ActionKind.Keys: Sender.Combo(a.Keys); break;
                    case ActionKind.Shortcut: if (param == "") return; Sender.Combo(param); break;
                    case ActionKind.Text: if (param == "") return; Sender.Text(param); break;
                    case ActionKind.Run:
                        if (param == "") return;
                        Process.Start(new ProcessStartInfo(param) { UseShellExecute = true });
                        break;
                    case ActionKind.Special:
                        switch (a.Id)
                        {
                            case "rclick": Sender.MouseClick(0x0008, 0x0010, 1); break;
                            case "mclick": Sender.MouseClick(0x0020, 0x0040, 1); break;
                            case "dblclick": Sender.MouseClick(0x0002, 0x0004, 2); break;
                            case "wheelup": Sender.Wheel(360); break;
                            case "wheeldown": Sender.Wheel(-360); break;
                            case "touchkb": TouchKeyboard.Toggle(); break;
                            case "lock": Native.LockWorkStation(); break;
                            case "menu": quickMenu.Show(Cursor.Position); return;
                            case "radial": radial.ShowAt(Cursor.Position, radialCfg.Items, radialHoldButton != null); return;
                            case "pause":
                                SetMapping(!mappingOn, true);
                                if (osdOn) osd.ShowMessage(mappingOn ? L.T("Mapping hervat", "Mapping resumed") : L.T("Mapping gepauzeerd", "Mapping paused"));
                                return;
                        }
                        break;
                }
                if (osdOn) osd.ShowMessage(a.Name);
            }
            catch (Exception ex)
            {
                if (osdOn) osd.ShowMessage(L.T("Mislukt: ", "Failed: ") + ex.Message);
            }
        }
    }
}
