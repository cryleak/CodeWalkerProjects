using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Linq;
using CodeWalker.GameFiles;

namespace CodeWalker.OIVInstaller
{
    public partial class MainForm : Form
    {
        private OivPackage _package;
        // Add-on install state — when these are set, _package is a synthetic
        // OivPackage built just for display and the install path routes through
        // AddonManager instead of OivInstaller.
        private string _addonSourcePath;
        private string _addonName;
        // Super OIV (.oivs) state — when set, _package is a synthetic display
        // package and the install path opens the selection wizard, then builds the
        // chosen modules/options into _package before running OivInstaller.
        private OivsPackage _oivsPackage;
        // Set by Program when launched with --manage (Uninstall.bat): after the
        // window shows (and any package/config has loaded) open Manage Mods.
        public bool OpenManageOnShown;
        private string _gameFolder = ""; // Current install target
        private string _spGameFolder = ""; // Actual GTA V folder
        private string _gameFolderLegacy = "";
        private string _gameFolderEnhanced = "";
        private MarqueePainter _marquee;
        private Animator _formFadeAnimator;
        private Animator _formResizeAnimator;
        private HeaderColorPulser _headerPulser;

        // AutoScaleMode.Font rescales every Designer coordinate at InitializeComponent
        // time (baseline 7x15 = Segoe UI 9pt at 96 DPI), but the runtime layout math in
        // this file was positioning the header/buttons/panels with raw designer-space
        // pixel constants. On displays scaled above 100% those raw values land inside
        // the already-scaled layout — header too short, title over the icon, and the
        // right-anchored header buttons overlapping each other. Every runtime layout
        // constant must go through SX/SY.
        private readonly float _scaleX = 1f;
        private readonly float _scaleY = 1f;
        private int SX(int designPx) => (int)Math.Round(designPx * _scaleX);
        private int SY(int designPx) => (int)Math.Round(designPx * _scaleY);

        public MainForm()
        {
            InitializeComponent();
            _scaleX = CurrentAutoScaleDimensions.Width / 7F;
            _scaleY = CurrentAutoScaleDimensions.Height / 15F;
            // Hide the original lblPackageName — we paint the title text ourselves on
            // pnlTitleClipping for sub-pixel scrolling smoothness. The Label still owns
            // font/color which the painter mirrors.
            this.Opacity = 0; // fade in on Shown
            AttachButtonAnimations();
            SetupMarqueePainter();
            SetupEmptyState();
            SetupTitleClipResizing();
            AttachSkipBackupConfirm();
        }

        // The title clip is fixed-width in the Designer (235px) but with a resizable
        // form, the header buttons are anchored Top|Right so they slide further from
        // the title as the form widens — and during the empty→loaded slide tween the
        // gap between title-left and Docs-left changes too. Reactively size the clip
        // to fill that gap so the marquee always uses the full available space.
        private void SetupTitleClipResizing()
        {
            EventHandler refresh = (s, e) => UpdateTitleClipWidth();
            btnDocs.LocationChanged += refresh;
            pnlTitleClipping.LocationChanged += refresh;
            this.SizeChanged += refresh;
        }

        private void UpdateTitleClipWidth()
        {
            if (pnlTitleClipping == null || btnDocs == null) return;
            int target = btnDocs.Left - pnlTitleClipping.Left - SX(12);
            int minWidth = SX(80);
            if (target < minWidth) target = minWidth;
            if (pnlTitleClipping.Width != target)
            {
                pnlTitleClipping.Width = target;
                pnlTitleClipping.Invalidate();
            }
        }

        // Empty-state landing view: a dashed-border card with package glyph + hints.
        // Shown until a package is loaded; replaced by description/info/additional via
        // the crossfade transition in DisplayPackageInfo.
        private void SetupEmptyState()
        {
            lblEmptyIcon.Text = ""; // OpenFile glyph (Segoe MDL2 Assets)
            panelEmptyState.Paint += OnEmptyStatePaint;
            panelEmptyState.SizeChanged += (s, e) =>
            {
                CenterEmptyStateContent();
                // Panel doesn't fully invalidate on resize by default, so the previously
                // drawn dashed border ghosts in the newly exposed area. Force repaint.
                panelEmptyState.Invalidate();
            };
            // Initial centering is performed in MainForm_Load — the window handle isn't
            // created yet here, so we can't BeginInvoke onto the UI thread.
        }

        private void OnEmptyStatePaint(object sender, System.Windows.Forms.PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rect = panelEmptyState.ClientRectangle;
            rect.Inflate(-1, -1);
            using (var path = RoundedRectPath(rect, 8))
            using (var pen = new System.Drawing.Pen(Color.FromArgb(205, 212, 220), 1.4f))
            {
                pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Custom;
                pen.DashPattern = new float[] { 5f, 4f };
                g.DrawPath(pen, path);
            }
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundedRectPath(Rectangle rect, int radius)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            int d = radius * 2;
            path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // The "Mod Package:" / "GTA V Folder:" labels are AutoSize, so their widths
        // aren't known until first layout. Align both textboxes (and the status labels
        // below them) just after the *wider* of the two labels so the gap is tight
        // and consistent regardless of label text. Textboxes are anchored Top|Left|
        // Right so changing .Left expands their Width to keep the Browse button gap.
        private void AlignPathFieldsToLabels()
        {
            int rightOfWiderLabel = Math.Max(Math.Max(lblOivLabel.Right, lblGameFolderLabel.Right), lblParagonVersion.Right);
            int textboxLeft = rightOfWiderLabel + 6;
            txtOivPath.Left = textboxLeft;
            txtGameFolder.Left = textboxLeft;
            cmbParagonVersion.Left = textboxLeft;
            lblGameStatus.Left = textboxLeft;
            lblAsiStatus.Left = textboxLeft;
        }

        private void CenterEmptyStateContent()
        {
            if (panelEmptyState == null || panelEmptyState.IsDisposed) return;
            int pw = panelEmptyState.ClientSize.Width;
            int ph = panelEmptyState.ClientSize.Height;
            int gapIconTitle = 6;
            int gapTitleSub = 4;
            int totalH = lblEmptyIcon.Height + gapIconTitle + lblEmptyTitle.Height + gapTitleSub + lblEmptySubtitle.Height;
            int y = Math.Max(8, (ph - totalH) / 2);

            lblEmptyIcon.Left = (pw - lblEmptyIcon.Width) / 2;
            lblEmptyIcon.Top = y;
            lblEmptyTitle.Left = (pw - lblEmptyTitle.Width) / 2;
            lblEmptyTitle.Top = lblEmptyIcon.Bottom + gapIconTitle;
            lblEmptySubtitle.Left = (pw - lblEmptySubtitle.Width) / 2;
            lblEmptySubtitle.Top = lblEmptyTitle.Bottom + gapTitleSub;
        }

        private void AttachSkipBackupConfirm()
        {
            // First-time confirm: if the user ticks Skip Backup, make sure they understand
            // that this install won't appear in Manage Mods and can't be reverted from the UI.
            // No persistence — they get the prompt every time they re-enable it within a session.
            bool confirmed = false;
            chkSkipBackup.CheckedChanged += (s, e) =>
            {
                if (!chkSkipBackup.Checked) { confirmed = false; return; }
                if (confirmed) return;
                var result = MessageBox.Show(
                    "Skipping backup creation means:\n\n" +
                    "• No backup of the game files this install touches\n" +
                    "• This install won't show up in Manage Mods\n" +
                    "• You won't be able to uninstall it through this app\n\n" +
                    "Continue with backup disabled?",
                    "Skip Backup",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (result == DialogResult.Yes) { confirmed = true; }
                else { chkSkipBackup.Checked = false; }
            };
        }

        private void SetupMarqueePainter()
        {
            // Replace the legacy 1px-step Timer marquee with a custom-painted, time-based
            // sub-pixel renderer. lblPackageName becomes invisible but its font/color are
            // still the source of truth so theme code keeps working unchanged.
            lblPackageName.Visible = false;
            _marquee = new MarqueePainter(pnlTitleClipping, lblPackageName.Font)
            {
                ForeColor = lblPackageName.ForeColor,
                Text = lblPackageName.Text,
            };
            _marquee.Start();
            // Stop the legacy timer (still wired in Designer); keep the field around so
            // the Designer-generated InitializeComponent doesn't need editing.
            tmrMarquee.Stop();
        }

        // Mirror the (still-hidden) lblPackageName state onto the marquee painter so
        // that DisplayPackageInfo's existing assignments to lblPackageName.Text / ForeColor
        // continue to drive the visible title without changes elsewhere.
        private void SyncMarqueeFromLabel()
        {
            if (_marquee == null) return;
            _marquee.Text = lblPackageName.Text ?? "";
            _marquee.ForeColor = lblPackageName.ForeColor;
            _marquee.Font = lblPackageName.Font;
            _marquee.ResetPosition();
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            // Empty-state labels are AutoSize, so we wait until the form's first layout
            // pass (Load fires after the handle is created and initial layout has run)
            // before measuring + centering them.
            CenterEmptyStateContent();
            AlignPathFieldsToLabels();

            // Idle-state header animation — slow R→G→B→R rainbow loop while no
            // package is loaded. Stops the moment a package is picked.
            _headerPulser = new HeaderColorPulser(
                panelHeader,
                Color.FromArgb(220, 60, 60),    // red
                Color.FromArgb(60, 180, 90),    // green
                Color.FromArgb(0, 120, 215));   // blue (Windows accent)
            _headerPulser.CycleMs = 12000;
            _headerPulser.Start();

            // Load the saved game folder FIRST, so that a package opened from the
            // command line (Install.bat) sees a valid _gameFolder and enables the
            // Install button. (UpdateInstallButton requires both a package and a
            // game folder; loading the package before the config left it disabled.)
            LoadConfig();

            // Check command line args for a package file. Scan all args (not just
            // args[1]) so the package path can appear in any position relative to
            // flags like --manage.
            var args = Environment.GetCommandLineArgs();
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (!File.Exists(a)) continue;
                if (a.EndsWith(".oivs", StringComparison.OrdinalIgnoreCase))
                {
                    txtOivPath.Text = a;
                    LoadOivsPackage(a);
                    break;
                }
                if (a.EndsWith(".oiv", StringComparison.OrdinalIgnoreCase) || a.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
                {
                    txtOivPath.Text = a;
                    LoadOivPackage(a);
                    break;
                }
            }

            // Final safety: reflect the resolved package + game-folder state.
            UpdateInstallButton();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // Release extracted package temp folders (.oivs/.oiv unpack to %TEMP% on
            // load — ~1 GB+ for large packs). Without this they accumulate until reboot.
            try { _package?.Dispose(); } catch { }
            try { _oivsPackage?.Dispose(); } catch { }
            base.OnFormClosed(e);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Smooth opacity fade-in. Cheap polish — masks the brief paint-storm of the
            // first WM_PAINT pass and gives the window a "settled" entrance.
            _formFadeAnimator?.Dispose();
            _formFadeAnimator = new Animator();
            _formFadeAnimator.Tween(220, t =>
            {
                if (this.IsDisposed) return;
                this.Opacity = t;
            }, () =>
            {
                if (!this.IsDisposed) this.Opacity = 1.0;
            }, Easing.EaseOutCubic);

            // Uninstall.bat launches with --manage: jump straight to Manage Mods
            // once the window (and any loaded package/config) has settled.
            if (OpenManageOnShown)
            {
                OpenManageOnShown = false;
                BeginInvoke((MethodInvoker)(() => btnUninstall_Click(this, EventArgs.Empty)));
            }
        }

        /// <summary>
        /// Wires smooth color-blend hover/press animations onto the form's buttons.
        /// Header buttons are white-on-accent; browse buttons sit on the white content
        /// area so their hover shades are slightly different.
        /// </summary>
        private void AttachButtonAnimations()
        {
            // White header buttons (Install / Manage Mods / Docs) — hover slightly off-white,
            // press a touch deeper. Subtle so it reads as polish, not as state change.
            var headerIdle = Color.White;
            var headerHover = Color.FromArgb(240, 244, 250);
            var headerPress = Color.FromArgb(225, 232, 242);

            ButtonHoverAnimator.Attach(btnInstall, headerIdle, headerHover, headerPress);
            ButtonHoverAnimator.Attach(btnUninstall, headerIdle, headerHover, headerPress);
            ButtonHoverAnimator.Attach(btnDocs, headerIdle, headerHover, headerPress);

            // Browse buttons use the system control face — give them a faint blue tint on hover.
            var browseIdle = SystemColors.Control;
            var browseHover = Color.FromArgb(225, 235, 248);
            var browsePress = Color.FromArgb(205, 220, 240);

            // These default to UseVisualStyleBackColor=true; flip off so our BackColor wins.
            btnBrowseOiv.UseVisualStyleBackColor = false;
            btnBrowseGame.UseVisualStyleBackColor = false;
            btnBrowseOiv.FlatStyle = FlatStyle.Flat;
            btnBrowseGame.FlatStyle = FlatStyle.Flat;
            btnBrowseOiv.FlatAppearance.BorderColor = Color.FromArgb(180, 180, 180);
            btnBrowseGame.FlatAppearance.BorderColor = Color.FromArgb(180, 180, 180);

            ButtonHoverAnimator.Attach(btnBrowseOiv, browseIdle, browseHover, browsePress);
            ButtonHoverAnimator.Attach(btnBrowseGame, browseIdle, browseHover, browsePress);
            ButtonHoverAnimator.Attach(btnDone, headerIdle, headerHover, headerPress);
        }

        private void MainForm_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files.Length == 1 && IsAcceptableDrop(files[0]))
                {
                    e.Effect = DragDropEffects.Copy;
                    return;
                }
            }
            e.Effect = DragDropEffects.None;
        }

        // Accept four kinds of drops:
        //   .oiv file                          → OIV package install
        //   .rpf file (not named "dlc.rpf")    → OIV-as-RPF package install
        //   dlc.rpf file                       → add-on install (prompt for name)
        //   folder containing dlc.rpf          → add-on install (folder name as add-on name)
        private static bool IsAcceptableDrop(string path)
        {
            if (Directory.Exists(path))
                return File.Exists(Path.Combine(path, "dlc.rpf"));
            if (!File.Exists(path)) return false;
            if (path.EndsWith(".oiv", StringComparison.OrdinalIgnoreCase)) return true;
            if (path.EndsWith(".oivs", StringComparison.OrdinalIgnoreCase)) return true;
            if (path.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private void MainForm_DragDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files.Length != 1) return;
            string path = files[0];

            if (Directory.Exists(path))
            {
                // Folder must contain dlc.rpf — IsAcceptableDrop already validated.
                string addonName = new DirectoryInfo(path).Name;
                if (!AddonManager.IsValidAddonName(addonName, out string err))
                {
                    MessageBox.Show(
                        $"Folder name '{addonName}' isn't a valid add-on name:\n\n{err}\n\nRename the folder and try again.",
                        "Invalid add-on name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                LoadAddon(path, addonName);
                return;
            }

            if (Path.GetFileName(path).Equals("dlc.rpf", StringComparison.OrdinalIgnoreCase))
            {
                // Bare dlc.rpf — prompt for the destination folder name.
                string suggested = Path.GetFileName(Path.GetDirectoryName(path)) ?? "";
                using (var dlg = new AddonNameDialog(suggested))
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        LoadAddon(path, dlg.AddonName);
                    }
                }
                return;
            }

            if (path.EndsWith(".oivs", StringComparison.OrdinalIgnoreCase))
            {
                txtOivPath.Text = path;
                LoadOivsPackage(path);
                return;
            }

            if (path.EndsWith(".oiv", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
            {
                txtOivPath.Text = path;
                LoadOivPackage(path);
            }
        }
        
        private void btnBrowseOiv_Click(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Select OIV / Super OIV / RPF Package";
                dlg.Filter = "All Packages (*.oiv;*.oivs;*.rpf)|*.oiv;*.oivs;*.rpf|Super OIV (*.oivs)|*.oivs|OIV/RPF (*.oiv;*.rpf)|*.oiv;*.rpf|All Files (*.*)|*.*";

                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    txtOivPath.Text = dlg.FileName;
                    if (dlg.FileName.EndsWith(".oivs", StringComparison.OrdinalIgnoreCase))
                        LoadOivsPackage(dlg.FileName);
                    else
                        LoadOivPackage(dlg.FileName);
                }
            }
        }
        
        private void btnBrowseGame_Click(object sender, EventArgs e)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Select GTA V Game Folder";
                if (_package != null && _package.IsFiveM)
                {
                    dlg.Description = "Select FiveM Application Data Folder (contains FiveM.app or FiveM.exe)";
                }
                dlg.ShowNewFolderButton = false;
                
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    string selected = dlg.SelectedPath;
                    if (_package != null && _package.IsFiveM)
                    {
                        string modsFolder = selected;
                        if (Directory.Exists(Path.Combine(selected, "FiveM.app")))
                        {
                            modsFolder = Path.Combine(selected, "FiveM.app", "mods");
                        }
                        else if (File.Exists(Path.Combine(selected, "FiveM.exe")) && !Path.GetFileName(selected).Equals("FiveM.app", StringComparison.OrdinalIgnoreCase))
                        {
                            if (Directory.Exists(Path.Combine(selected, "FiveM.app")))
                            {
                                modsFolder = Path.Combine(selected, "FiveM.app", "mods");
                            }
                            else
                            {
                                modsFolder = Path.Combine(selected, "mods");
                            }
                        }
                        else if (!Path.GetFileName(selected).Equals("mods", StringComparison.OrdinalIgnoreCase))
                        {
                            modsFolder = Path.Combine(selected, "mods");
                        }
                        selected = modsFolder;
                    }

                    txtGameFolder.Text = selected;
                    _gameFolder = selected;
                    _spGameFolder = selected; // User explicitly selected this

                    // Update specific version slot if we know context
                    if (_package != null && _package.Metadata.GameVersion == GameVersion.Enhanced)
                    {
                        _gameFolderEnhanced = _gameFolder;
                    }
                    else if (_package != null && _package.Metadata.GameVersion == GameVersion.Legacy)
                    {
                        _gameFolderLegacy = _gameFolder;
                    }
                    else
                    {
                        // Fallback: If no package context, assume Legacy/Base is the primary one people set
                        // Or maybe update both if they are empty?
                        if (string.IsNullOrEmpty(_gameFolderLegacy)) _gameFolderLegacy = _gameFolder;
                    }

                    ValidateGameFolder();
                    UpdateInstallButton();
                    SaveConfig(); // Save on manual selection
                }
            }
        }
        
        // GetConfigPath removed, logic moved to OivAppConfig
        
        private void LoadConfig()
        {
             var config = OivAppConfig.Load();
             if (config != null)
             {
                 if (!string.IsNullOrEmpty(config.LastGameFolder) && Directory.Exists(config.LastGameFolder))
                 {
                     _gameFolder = config.LastGameFolder;
                     _spGameFolder = config.LastGameFolder;
                     txtGameFolder.Text = _gameFolder;
                     ValidateGameFolder();
                 }
                 
                 _gameFolderLegacy = config.GameFolderLegacy;
                 _gameFolderEnhanced = config.GameFolderEnhanced;
             }
        }
        
        private void SaveConfig()
        {
            string saveFolder = !string.IsNullOrEmpty(_spGameFolder) ? _spGameFolder : _gameFolder;
            if (string.IsNullOrEmpty(saveFolder)) return;

            var config = OivAppConfig.Load(); // Reload to keep other changes? Or just overwrite?
            // Simple overwrite for now as we are single instance typically
            config.LastGameFolder = saveFolder;
            config.GameFolderLegacy = _gameFolderLegacy;
            config.GameFolderEnhanced = _gameFolderEnhanced;
            
            OivAppConfig.Save(config);
        }
        
        // Internal OivConfig class removed

        private void btnUninstall_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_gameFolder) || !Directory.Exists(_gameFolder))
            {
                MessageBox.Show("Please select a valid GTA V folder first.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Pass BOTH the SP folder and the FiveM folder
            string spParam = !string.IsNullOrEmpty(_spGameFolder) && Directory.Exists(_spGameFolder) ? _spGameFolder : null;
            // If current _gameFolder is NOT FiveM, it might be SP, so use that if _spGameFolder is empty
            if (spParam == null && !string.IsNullOrEmpty(_gameFolder) && !_gameFolder.Contains("FiveM.app"))
                spParam = _gameFolder;

            using (var form = new UninstallForm(spParam, FiveMHelper.GetFiveMModsFolder()))
            {
                form.ShowDialog(this);
            }
        }

        private void btnDocs_Click(object sender, EventArgs e)
        {
            using (var form = new DocumentationForm())
            {
                form.ShowDialog(this);
            }
        }

        private void linkInstructions_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            if (_package == null) return;
            using (var form = new InstructionsForm(_package))
            {
                form.ShowDialog(this);
            }
        }

        // Designer wires this handler to tmrMarquee.Tick. Marquee scrolling now lives in
        // MarqueePainter (custom paint with sub-pixel positioning); the timer itself is
        // stopped in SetupMarqueePainter so this body never actually runs. Kept as a
        // no-op so the Designer-generated event subscription still resolves.
        private void tmrMarquee_Tick(object sender, EventArgs e) { }
        
        private void ValidateGameFolder()
        {
            if (string.IsNullOrEmpty(_gameFolder))
            {
                lblGameStatus.Text = "";
                lblGameStatus.ForeColor = Color.Gray;
                lblAsiStatus.Text = "";
                return;
            }

            bool hasLegacy = File.Exists(Path.Combine(_gameFolder, "GTA5.exe"));
            bool hasEnhanced = File.Exists(Path.Combine(_gameFolder, "GTA5_Enhanced.exe"));
            
            bool hasOpenIV = File.Exists(Path.Combine(_gameFolder, "OpenIV.asi"));
            bool hasOpenRPF = File.Exists(Path.Combine(_gameFolder, "OpenRPF.asi"));
            // RageOpenV is a unified open-source replacement (https://github.com/Chiheb-Bacha/RageOpenV)
            // that covers both Legacy (gen8, formerly OpenIV.asi) and Enhanced (gen9, formerly OpenRPF.asi)
            // mods-folder loading. Treat its presence as satisfying either requirement.
            bool hasRageOpenV = File.Exists(Path.Combine(_gameFolder, "RageOpenV.asi"));
            bool hasDinput8 = File.Exists(Path.Combine(_gameFolder, "dinput8.dll"));
            bool hasXinput = File.Exists(Path.Combine(_gameFolder, "xinput1_4.dll"));
            
            string asiStatus = "";
            
            // Check what version the package requires
            var requiredVersion = _package?.Metadata?.GameVersion ?? GameVersion.Any;
            
            if (_package != null && _package.IsFiveM)
            {
                // FiveM validation
                if (string.IsNullOrEmpty(_gameFolder))
                {
                    lblGameStatus.Text = "⚠ FiveM mods folder not found";
                    lblGameStatus.ForeColor = Color.Orange;
                }
                else
                {
                    lblGameStatus.Text = "✓ FiveM mods folder selected";
                    lblGameStatus.ForeColor = Color.Green;
                }
                lblAsiStatus.Text = "";
                return;
            }
            
            if (hasEnhanced)
            {
                if (requiredVersion == GameVersion.Legacy)
                {
                    lblGameStatus.Text = "⚠ Package requires GTA V Legacy, but this is Enhanced";
                    lblGameStatus.ForeColor = Color.Orange;
                }
                else
                {
                    lblGameStatus.Text = "✓ Valid GTA V folder (Enhanced)";
                    lblGameStatus.ForeColor = Color.Green;
                    
                }
            }
            else if (hasLegacy)
            {
                if (requiredVersion == GameVersion.Enhanced)
                {
                    lblGameStatus.Text = "⚠ Package requires GTA V Enhanced, but this is Legacy";
                    lblGameStatus.ForeColor = Color.Orange;
                }
                else
                {
                    lblGameStatus.Text = "✓ Valid GTA V folder (Legacy)";
                    lblGameStatus.ForeColor = Color.Green;

                }
            }
            else
            {
                lblGameStatus.Text = "⚠ GTA5.exe or GTA5_Enhanced.exe not found";
                lblGameStatus.ForeColor = Color.Orange;
            }
            
            // Update ASI status label
            if (!string.IsNullOrEmpty(asiStatus))
            {
                lblAsiStatus.Text = asiStatus;
                lblAsiStatus.ForeColor = Color.Red;
            }
            else if (hasEnhanced || hasLegacy)
            {
                 // Prefer to surface RageOpenV when present since it's the unified loader
                 // covering both versions; otherwise fall back to the version-specific name.
                 if (hasRageOpenV)
                 {
                     lblAsiStatus.Text = "✓ RageOpenV.asi installed";
                     lblAsiStatus.ForeColor = Color.Green;
                 }
                 else if (hasEnhanced && hasOpenRPF)
                 {
                     lblAsiStatus.Text = "✓ OpenRPF.asi installed";
                     lblAsiStatus.ForeColor = Color.Green;
                 }
                 else if (hasLegacy && hasOpenIV)
                 {
                     lblAsiStatus.Text = "✓ OpenIV.asi installed";
                     lblAsiStatus.ForeColor = Color.Green;
                 }
                 else
                 {
                     lblAsiStatus.Text = "";
                 }
            }
            else
            {
                lblAsiStatus.Text = "";
            }

            
            // Enable Manage Mods if game folder is potentially valid (has exe)
            if (btnUninstall != null)
            {
                btnUninstall.Enabled = hasLegacy || hasEnhanced;
            }
        }

        /// <summary>
        /// Sets up the main view to display a *DLC add-on* drop — a folder containing
        /// dlc.rpf, or a bare dlc.rpf that the user has just named. Construct a minimal
        /// stand-in <see cref="OivPackage"/> so the existing loaded-state layout
        /// (description, info, header) renders, then override the package-specific
        /// labels so the user understands this is an add-on, not an OIV install.
        /// </summary>
        private void LoadAddon(string sourcePath, string addonName)
        {
            _package?.Dispose();
            _package = null;
            _oivsPackage?.Dispose();
            _oivsPackage = null;

            _addonSourcePath = sourcePath;
            _addonName = addonName;

            var pkg = new OivPackage();
            pkg.Metadata.Name = addonName;
            pkg.Metadata.Description =
                "DLC add-on package." + Environment.NewLine + Environment.NewLine +
                "Destination:" + Environment.NewLine +
                $"  mods\\update\\x64\\dlcpacks\\{addonName}\\" + Environment.NewLine + Environment.NewLine +
                "Will be enabled in dlclist.xml inside update.rpf on install.";
            _package = pkg;

            txtOivPath.Text = sourcePath;
            DisplayPackageInfo();

            // Override package-specific labels for add-on mode. The Info / Additional
            // panels still appear (kept the same layout) but with add-on phrasing.
            linkAuthor.Text = "";
            linkAuthor.Tag = null;
            lblGame.Text = "DLC Add-on";
            lblGame.ForeColor = Color.FromArgb(180, 100, 30);
            lblVersion.Text = "—";

            // No assembly.xml to preview — hide the "View install steps" link.
            linkInstructions.Visible = false;

            UpdateInstallButton();
        }

        // Loads a Super OIV (.oivs): builds a synthetic display package so the
        // existing header/description/theming renders unchanged, while _oivsPackage
        // holds the real manifest for the selection wizard at install time.
        private void LoadOivsPackage(string path)
        {
            try
            {
                _package?.Dispose();
                _package = null;
                _oivsPackage?.Dispose();
                _oivsPackage = null;
                _addonName = null;
                _addonSourcePath = null;

                _oivsPackage = OivsPackage.Load(path);
                _package = OivPackage.CreateSynthetic(
                    _oivsPackage.Metadata, _oivsPackage.ContentPath, new List<OivOperation>(),
                    _oivsPackage.IconData);

                DisplayPackageInfo();

                // The wizard is the install-steps preview for .oivs packages.
                linkInstructions.Visible = false;

                UpdateInstallButton();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load Super OIV package:\n\n{ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void LoadOivPackage(string path)
        {
            try
            {
                // Dispose previous package
                _package?.Dispose();
                _package = null;
                _oivsPackage?.Dispose();
                _oivsPackage = null;

                // Check if it's a folder (extracted OIV for testing)
                if (Directory.Exists(path))
                {
                    _package = OivPackage.LoadFromFolder(path);
                }
                else
                {
                    _package = OivPackage.Load(path);
                }
                
                DisplayPackageInfo();

                if (_package.IsFiveM)
                {
                    string fivemMods = FiveMHelper.GetFiveMModsFolder();
                    if (!string.IsNullOrEmpty(fivemMods))
                    {
                        _gameFolder = fivemMods;
                        txtGameFolder.Text = _gameFolder;
                    }
                    else
                    {
                        // Fallback or just show empty? 
                        // If FiveM app exists but mods folder doesn't, we can create it?
                        // FiveMHelper checks existance. 
                        // Let's manually construct it if FiveM is installed.
                        if (FiveMHelper.IsFiveMInstalled())
                        {
                            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                            string fiveMAppPath = Path.Combine(localAppData, "FiveM", "FiveM.app");
                            _gameFolder = Path.Combine(fiveMAppPath, "mods");
                            txtGameFolder.Text = _gameFolder;
                        }
                    }
                    ValidateGameFolder();
                }
                
                UpdateInstallButton();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load OIV package:\n\n{ex.Message}", "Error", 
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void DisplayPackageInfo()
        {
            if (_package == null) return;

            var meta = _package.Metadata;
            bool wasEmpty = panelEmptyState != null && panelEmptyState.Visible;

            // A package is now loaded — halt the idle pulse so the theme color
            // (default blue or the package's HeaderBackground) holds steady.
            _headerPulser?.Stop();

            // Update window title
            this.Text = $"{meta.Name} - Package Installer";

            // Header
            lblPackageName.Text = meta.Name;
            lblAuthor.Text = meta.AuthorDisplayName;

            // Description
            rtbDescription.Text = meta.Description.Trim();

            // Calculate height with a cap
            Size textSize = TextRenderer.MeasureText(rtbDescription.Text, rtbDescription.Font, new Size(rtbDescription.Width, 0), TextFormatFlags.WordBreak);

            // Cap height at 250px (scroll if larger), min 35px
            int targetHeight = Math.Min(Math.Max(textSize.Height + SY(20), SY(35)), SY(250));

            rtbDescription.Height = targetHeight;

            // Compute the *loaded-state* positions for the secondary panels. When leaving
            // the empty state we defer applying these so the crossfade snapshot still
            // shows the empty-state layout — otherwise panelPaths would visibly teleport
            // before the fade kicks in.
            int spacer = SY(20);
            int linkY = rtbDescription.Bottom + SY(4);
            int linkH = Math.Max(linkInstructions.Height, SY(17));
            int loadedPathsTop = linkY + linkH + SY(10);
            int loadedInfoTop = loadedPathsTop + panelPaths.Height + spacer;
            int requiredClientHeight = SY(100) + loadedInfoTop + panelInfo.Height + SY(30);

            // Hardcoded minimum height to ensure basic UI usability
            int minHeight = SY(460);

            // Apply the required height, respecting the minimum
            int finalHeight = Math.Max(minHeight, requiredClientHeight);

            if (!wasEmpty)
            {
                linkInstructions.Location = new Point(SX(20), linkY);
                panelPaths.Top = loadedPathsTop;
                panelInfo.Top = loadedInfoTop;
                panelAdditional.Top = loadedInfoTop;
                if (this.ClientSize.Height != finalHeight)
                {
                    AnimateClientHeight(finalHeight, durationMs: 260);
                }
            }
            
            // Information section
            linkAuthor.Text = meta.AuthorDisplayName;
            linkAuthor.Tag = meta.AuthorActionLink;
            lblVersion.Text = meta.Version;
            
            // Update supported game based on package version
            switch (meta.GameVersion)
            {
                case GameVersion.Enhanced:
                    lblGame.Text = "GTA V Enhanced";
                    break;
                case GameVersion.Legacy:
                    lblGame.Text = "GTA V Legacy";
                    break;
                default:
                    lblGame.Text = "GTA V";
                    break;
            }

            if (_package.IsFiveM)
            {
                lblGame.Text = "FiveM";
                lblGame.ForeColor = Color.OrangeRed;
            }
            else
            {
                lblGame.ForeColor = Color.Black; 
            }
            
            // Re-validate game folder if already set
            if (!string.IsNullOrEmpty(_gameFolder))
            {
                ValidateGameFolder();
            }
            
            // Set icon if available
            if (_package.IconData != null && _package.IconData.Length > 0)
            {
                try
                {
                    using (var ms = new MemoryStream(_package.IconData))
                    {
                        picIcon.Image = Image.FromStream(ms);
                    }
                }
                catch { }
            }

            // Reset default theme (Standard Blue)
            Color defaultBlue = Color.FromArgb(0, 120, 215);
            linkAuthor.LinkColor = defaultBlue;
            linkWeb.LinkColor = defaultBlue;
            linkYoutube.LinkColor = defaultBlue;
            btnInstall.ForeColor = Color.Black; 
            btnInstall.FlatAppearance.BorderColor = Color.FromArgb(180, 180, 180);
            panelHeader.BackColor = defaultBlue;
            lblPackageName.ForeColor = Color.White;
            lblAuthor.ForeColor = Color.White;
            lblWarning.ForeColor = Color.White;

            // Apply header color if specified
            if (!string.IsNullOrEmpty(meta.HeaderBackground))
            {
                try
                {
                    string colorStr = meta.HeaderBackground.TrimStart('$');
                    if (colorStr.Length == 8)
                    {
                        int argb = Convert.ToInt32(colorStr, 16);
                        Color headerColor = Color.FromArgb(argb);
                        // WinForms doesn't support true alpha transparency on panels.
                        // Force fully opaque to avoid mismatched child control backgrounds.
                        headerColor = Color.FromArgb(255, headerColor.R, headerColor.G, headerColor.B);
                        panelHeader.BackColor = headerColor;
                        
                        Color textColor = meta.UseBlackTextColor ? Color.Black : Color.White;
                        lblPackageName.ForeColor = textColor;
                        lblAuthor.ForeColor = textColor;
                        lblWarning.ForeColor = Color.FromArgb(textColor.A, 
                            (int)(textColor.R * 0.8), (int)(textColor.G * 0.8), (int)(textColor.B * 0.8));
                            
                        // Apply dynamic theme to content controls (if Header is dark enough to be visible on white)
                        // If UseBlackTextColor is FALSE, it means Header is Dark (White text used). 
                        // Dark colors work well as text/accents on White backgrounds.
                        if (!meta.UseBlackTextColor)
                        {
                            linkAuthor.LinkColor = headerColor;
                            linkWeb.LinkColor = headerColor;
                            linkYoutube.LinkColor = headerColor;
                            
                            btnInstall.ForeColor = headerColor;
                            btnInstall.FlatAppearance.BorderColor = headerColor;
                        }
                    }
                }
                catch { }
            }
            
            // Additional links
            DisplayAuthorLinks();

            // Sync the marquee with whatever lblPackageName ended up showing — text and
            // (theme-driven) ForeColor — and reset its scroll position for a fresh sweep.
            SyncMarqueeFromLabel();

            // Empty → loaded transition. Crossfade panelContent so the empty-state card
            // smoothly hands off to the description/info/additional sections (and the
            // panelPaths repositioning), while the header grows from 70→100 and slides
            // the title back to its loaded position alongside the form's height tween.
            if (wasEmpty)
            {
                ViewTransitions.CrossFade(panelContent, () =>
                {
                    linkInstructions.Location = new Point(SX(20), linkY);
                    linkInstructions.Visible = true;
                    panelPaths.Top = loadedPathsTop;
                    panelInfo.Top = loadedInfoTop;
                    panelAdditional.Top = loadedInfoTop;
                    panelEmptyState.Visible = false;
                    rtbDescription.Visible = true;
                    panelInfo.Visible = true;
                    panelAdditional.Visible = true;
                    picIcon.Visible = true;
                    lblAuthor.Visible = true;
                });
                AnimateLayoutTransition(finalHeight, targetHeaderHeight: SY(100), durationMs: 280);
            }
        }

        /// <summary>
        /// One-shot transition tween covering the form's ClientSize.Height, the header's
        /// Height, and the title clip's location — used when leaving the empty state so
        /// the header can grow (70→100) and the title can slide back (24,18 → 93,15)
        /// while the form expands to fit the loaded layout.
        /// </summary>
        private void AnimateLayoutTransition(int targetClientHeight, int targetHeaderHeight, int durationMs)
        {
            // Empty → loaded: reveal Install from just off-screen right; end positions
            // are right-anchored against the current form width. Right margins are
            // design-space (Install 20, gaps of 10 between buttons at 100% scale) and
            // the buttons' actual (already-scaled) widths do the rest, so the row can't
            // self-overlap at 125/150% display scaling.
            int formWidth = this.ClientSize.Width;
            btnInstall.Left = formWidth;
            btnInstall.Visible = true;
            int installX = formWidth - btnInstall.Width - SX(20);
            int uninstallX = installX - btnUninstall.Width - SX(10);
            int docsX = uninstallX - btnDocs.Width - SX(10);
            AnimateHeaderTransition(
                targetClientHeight, targetHeaderHeight,
                targetTitle: new Point(SX(93), SY(15)),
                targetDocsX: docsX,
                targetUninstallX: uninstallX,
                targetInstallX: installX,
                hideInstallAtEnd: false,
                durationMs);
        }

        private void AnimateLayoutTransitionToEmpty(int durationMs)
        {
            // Loaded → empty: slide Install off-screen right and reset Docs/Manage Mods
            // to their empty-state slots (Manage Mods right margin 20, 10 gap to Docs,
            // in design-space pixels — actual scaled widths keep the row from
            // overlapping on high-DPI displays).
            int formWidth = this.ClientSize.Width;
            int uninstallX = formWidth - btnUninstall.Width - SX(20);
            int docsX = uninstallX - btnDocs.Width - SX(10);
            AnimateHeaderTransition(
                targetClientHeight: SY(380), targetHeaderHeight: SY(70),
                targetTitle: new Point(SX(24), SY(18)),
                targetDocsX: docsX,
                targetUninstallX: uninstallX,
                targetInstallX: formWidth,
                hideInstallAtEnd: true,
                durationMs);
        }

        /// <summary>
        /// Shared header-layout tween for both transition directions. Handles client
        /// height, header height, title clip location, and the three header buttons in
        /// one easing curve so the motion reads as a single coordinated reflow.
        /// </summary>
        private void AnimateHeaderTransition(
            int targetClientHeight, int targetHeaderHeight,
            Point targetTitle,
            int targetDocsX, int targetUninstallX, int targetInstallX,
            bool hideInstallAtEnd, int durationMs)
        {
            int startClient = this.ClientSize.Height;
            int startHeader = panelHeader.Height;
            Point startTitle = pnlTitleClipping.Location;
            int startDocsX = btnDocs.Left;
            int startUninstallX = btnUninstall.Left;
            int startInstallX = btnInstall.Left;

            int dClient = targetClientHeight - startClient;
            int dHeader = targetHeaderHeight - startHeader;
            int dTitleX = targetTitle.X - startTitle.X;
            int dTitleY = targetTitle.Y - startTitle.Y;
            int dDocsX = targetDocsX - startDocsX;
            int dUninstallX = targetUninstallX - startUninstallX;
            int dInstallX = targetInstallX - startInstallX;
            if (dClient == 0 && dHeader == 0 && dTitleX == 0 && dTitleY == 0
                && dDocsX == 0 && dUninstallX == 0 && dInstallX == 0)
            {
                if (hideInstallAtEnd) btnInstall.Visible = false;
                return;
            }

            _formResizeAnimator?.Dispose();
            _formResizeAnimator = new Animator();
            _formResizeAnimator.Tween(durationMs, t =>
            {
                if (this.IsDisposed) return;
                int h = startClient + (int)Math.Round(dClient * t);
                int hh = startHeader + (int)Math.Round(dHeader * t);
                int tx = startTitle.X + (int)Math.Round(dTitleX * t);
                int ty = startTitle.Y + (int)Math.Round(dTitleY * t);
                int dx = startDocsX + (int)Math.Round(dDocsX * t);
                int ux = startUninstallX + (int)Math.Round(dUninstallX * t);
                int ix = startInstallX + (int)Math.Round(dInstallX * t);
                if (panelHeader.Height != hh) panelHeader.Height = hh;
                if (this.ClientSize.Height != h)
                    this.ClientSize = new Size(this.ClientSize.Width, h);
                if (pnlTitleClipping.Left != tx || pnlTitleClipping.Top != ty)
                    pnlTitleClipping.Location = new Point(tx, ty);
                if (btnDocs.Left != dx) btnDocs.Left = dx;
                if (btnUninstall.Left != ux) btnUninstall.Left = ux;
                if (btnInstall.Left != ix) btnInstall.Left = ix;
            }, () =>
            {
                if (this.IsDisposed) return;
                panelHeader.Height = targetHeaderHeight;
                this.ClientSize = new Size(this.ClientSize.Width, targetClientHeight);
                pnlTitleClipping.Location = targetTitle;
                btnDocs.Left = targetDocsX;
                btnUninstall.Left = targetUninstallX;
                btnInstall.Left = targetInstallX;
                if (hideInstallAtEnd) btnInstall.Visible = false;
            }, Easing.EaseOutCubic);
        }

        /// <summary>
        /// Smoothly tweens the form's ClientSize.Height between current and target.
        /// Replaces the previous direct assignment so a new package's content size
        /// settles instead of snapping. Width is preserved.
        /// </summary>
        private void AnimateClientHeight(int targetHeight, int durationMs)
        {
            int start = this.ClientSize.Height;
            int delta = targetHeight - start;
            if (delta == 0) return;

            _formResizeAnimator?.Dispose();
            _formResizeAnimator = new Animator();
            _formResizeAnimator.Tween(durationMs, t =>
            {
                if (this.IsDisposed) return;
                int h = start + (int)Math.Round(delta * t);
                if (this.ClientSize.Height != h)
                    this.ClientSize = new Size(this.ClientSize.Width, h);
            }, () =>
            {
                if (!this.IsDisposed)
                    this.ClientSize = new Size(this.ClientSize.Width, targetHeight);
            }, Easing.EaseOutCubic);
        }

        private void DisplayAuthorLinks()
        {
            if (_package == null) return;
            
            var meta = _package.Metadata;
            
            // Web Link
            if (!string.IsNullOrEmpty(meta.AuthorWeb))
            {
                linkWeb.Tag = meta.AuthorWeb;
                linkWeb.Visible = true;
            }
            else
            {
                linkWeb.Visible = false;
            }
            
            // YouTube
            if (!string.IsNullOrEmpty(meta.AuthorYoutube))
            {
                string youtubeUrl = meta.AuthorYoutube;
                if (!youtubeUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    youtubeUrl = $"https://www.youtube.com/c/{meta.AuthorYoutube}";
                }
                linkYoutube.Tag = youtubeUrl;
                linkYoutube.Visible = true;
            }
            else
            {
                linkYoutube.Visible = false;
            }
        }
        
        private void UpdateInstallButton()
        {
            btnInstall.Enabled = _package != null && !string.IsNullOrEmpty(_gameFolder);
        }
        
        private void lblAuthor_Click(object sender, EventArgs e)
        {
            if (_package?.Metadata?.AuthorActionLink != null)
            {
                OpenUrl(_package.Metadata.AuthorActionLink);
            }
        }
        
        private void linkLabel_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            if (sender is LinkLabel link && link.Tag is string url && !string.IsNullOrEmpty(url))
            {
                OpenUrl(url);
            }
        }
        
        private void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open link: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void btnInstall_Click(object sender, EventArgs e)
        {
            if (_package == null || string.IsNullOrEmpty(_gameFolder)) return;

            // Check for running game process
            while (ProcessHelper.IsGameRunning(out string processName))
            {
                var result = MessageBox.Show(
                    $"The game process '{processName}' is currently running.\n\n" +
                    "Please close the game before installing mods to prevent file locking errors.",
                    "Game is Running",
                    MessageBoxButtons.RetryCancel,
                    MessageBoxIcon.Warning);

                if (result == DialogResult.Cancel) return;
            }

            // Super OIV path: open the selection wizard, then build the chosen
            // modules/options into _package and fall through to the standard
            // single-player install flow (existing-package prompt + OivInstaller).
            if (_oivsPackage != null)
            {
                using (var wizard = new OivsSelectionForm(_oivsPackage))
                {
                    if (wizard.ShowDialog(this) != DialogResult.OK) return;

                    var ops = _oivsPackage.BuildOperations(wizard.Selection);
                    if (ops.Count == 0)
                    {
                        MessageBox.Show("No components were selected to install.",
                            "Nothing to install", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    _package = OivPackage.CreateSynthetic(
                        _oivsPackage.Metadata, _oivsPackage.ContentPath, ops);
                }
            }

            // Add-on install path: copy folder/dlc.rpf into mods\update\x64\dlcpacks\<name>\
            // and enable it in dlclist.xml. No BackupSession is recorded, so the add-on
            // never appears in Manage Mods → Installed Packages.
            if (_addonName != null)
            {
                InstallAddonPath();
                return;
            }

            if (_package.IsFiveM)
            {
                // FiveM Installation Logic
                if (!Directory.Exists(_gameFolder))
                {
                    try 
                    {
                        Directory.CreateDirectory(_gameFolder);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to create mods folder: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }

                string sourcePath = _package.SourceRpf.FilePath;
                string destPath = Path.Combine(_gameFolder, Path.GetFileName(sourcePath));

                if (File.Exists(destPath))
                {
                    var result = MessageBox.Show(
                        $"File '{Path.GetFileName(destPath)}' already exists in FiveM mods folder.\nOverwrite?", 
                        "Confirm Overwrite", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (result == DialogResult.No) return;
                }

                try
                {
                    // Create uninstaller log
                    var manager = new BackupManager(_gameFolder);
                    var session = manager.CreateSession(
                        _package.Metadata.Name, 
                        _package.Metadata.Description, 
                        _package.Metadata.Version, 
                        false // FiveM RPFs are generally not "Gen9" in the console sense, or we don't care about encryption here
                    );

                    string fileName = Path.GetFileName(sourcePath);
                    session.TrackFileAdded(fileName);
                    
                    File.Copy(sourcePath, destPath, true);
                    
                    session.Save();
                    
                    MessageBox.Show("FiveM mod installed successfully!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to install mod: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return;
            }

            string paragonBuild = cmbParagonVersion.Text switch
            {
                "1.61" => "18893",
                "1.69" => "23359",
                "1.73" => "27223",
                _ => null,
            };
            bool useModsFolder = !chkNoModsFolder.Checked;
            bool compatibilityMode = chkCompatibilityMode.Checked;
            var encryption = (RpfEncryption)Enum.Parse(typeof(RpfEncryption), cmbRpfEncryption.Text);
            if (!useModsFolder && MessageBox.Show(
                    "This will modify RPF archives in the game folder directly. Continue?",
                    "Direct RPF Installation", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return;
            }
            string modsFolder = string.IsNullOrEmpty(paragonBuild)
                ? Path.Combine(_gameFolder, "mods")
                : Path.Combine(_gameFolder, "mods", "versions", paragonBuild);
            bool clearModsFolder = false;
            try
            {
                if (useModsFolder && Directory.Exists(modsFolder) && Directory.EnumerateFileSystemEntries(modsFolder).Any())
                {
                    var result = MessageBox.Show(
                        $"The current mods folder already contains files:\n\n{modsFolder}\n\nDelete its old contents before installing?",
                        "Existing Mods Found", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);
                    if (result == DialogResult.Cancel) return;
                    clearModsFolder = result == DialogResult.Yes;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not check the mods folder:\n\n{ex.Message}", "Mods Folder Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Check for existing installations with the same name
            var backupManager = new BackupManager(_gameFolder);
            var existingPackages = backupManager.GetInstalledPackages()
                .Where(x => x.PackageName == _package.Metadata.Name)
                .ToList();

            List<BackupLog> packagesToUninstall = new List<BackupLog>();
            UninstallMode uninstallMode = UninstallMode.Backup;

            if (existingPackages.Count > 0)
            {
                // We need a custom dialog for 3 choices: Uninstall (Backup), Uninstall (Vanilla), Keep
                using (var prompt = new Form())
                {
                    prompt.Font = this.Font;
                    prompt.Width = SX(500);
                    prompt.Height = SY(280);
                    prompt.Text = "Conflicting Installation Found";
                    prompt.StartPosition = FormStartPosition.CenterParent;
                    prompt.FormBorderStyle = FormBorderStyle.FixedDialog;
                    prompt.MaximizeBox = false;
                    prompt.MinimizeBox = false;

                    var iconBox = new PictureBox { Image = SystemIcons.Warning.ToBitmap(), Size = new Size(SX(32), SY(32)), SizeMode = PictureBoxSizeMode.Zoom, Location = new Point(SX(20), SY(20)) };
                    prompt.Controls.Add(iconBox);

                    var lbl = new Label
                    {
                        Text = $"Found {existingPackages.Count} existing installation(s) of '{_package.Metadata.Name}'.\n\n" +
                               "How would you like to proceed?",
                        Location = new Point(SX(70), SY(20)),
                        Size = new Size(SX(400), SY(60))
                    };
                    prompt.Controls.Add(lbl);

                    // Option 1: Revert to Backup (Standard)
                    var btnBackup = new Button
                    {
                        Text = "Uninstall & Revert to Backups\n(Standard Update)",
                        Location = new Point(SX(70), SY(90)),
                        Size = new Size(SX(380), SY(40)),
                        DialogResult = DialogResult.Yes
                    };
                    btnBackup.Click += (s, ev) => { uninstallMode = UninstallMode.Backup; prompt.Close(); };
                    prompt.Controls.Add(btnBackup);

                    // Option 2: Revert to Vanilla
                    var btnVanilla = new Button
                    {
                        Text = "Uninstall & Reset to Vanilla\n(Clean Reinstall)",
                        Location = new Point(SX(70), SY(135)),
                        Size = new Size(SX(380), SY(40)),
                        DialogResult = DialogResult.OK
                    };
                    btnVanilla.Click += (s, ev) => { uninstallMode = UninstallMode.Vanilla; prompt.Close(); };
                    prompt.Controls.Add(btnVanilla);

                    // Option 3: Keep (Stacking)
                    var btnKeep = new Button
                    {
                        Text = "Keep Existing Files\n(Install on top / Stacking)",
                        Location = new Point(SX(70), SY(180)),
                        Size = new Size(SX(380), SY(40)),
                        DialogResult = DialogResult.No
                    };
                    btnKeep.Click += (s, ev) => { prompt.Close(); };
                    prompt.Controls.Add(btnKeep);

                    var result = prompt.ShowDialog(this);
                    
                    if (result == DialogResult.Cancel) return; // Closed window
                    
                    if (result == DialogResult.Yes || result == DialogResult.OK)
                    {
                        packagesToUninstall = existingPackages;
                    }
                    else if (result == DialogResult.No)
                    {
                        // Proceed without uninstalling
                    }
                }
            }

            // Snapshot the skip-backup choice before we leave the UI thread.
            bool skipBackup = chkSkipBackup.Checked;

            // Switch to log view
            ShowInstallLog();

            // Bridge OivInstaller progress reports into the SmoothProgressBar on the UI thread.
            var progress = new Progress<InstallProgress>(p =>
            {
                if (progressBar.IsDisposed) return;
                progressBar.SetValue(p.Percent);
            });

            // Run installation in background to keep UI responsive
            Task.Run(() =>
            {
                try
                {
                    Log("Initializing installation...");
                    Log($"Package: {_package.Metadata.Name}");
                    Log($"Target: {_gameFolder}");
                    if (skipBackup)
                        Log("Backup creation: DISABLED (Manage Mods will not see this install)");

                    var installer = new OivInstaller(_gameFolder, _package, message => Log(message),
                        paragonBuild, encryption, useModsFolder, compatibilityMode);
                    installer.Install(progress, packagesToUninstall, uninstallMode, skipBackup, clearModsFolder);
                    
                    Log(""); // Spacer
                    Log("Installation completed successfully.");
                    Log("----------------------------------------");
                }
                catch (Exception ex)
                {
                    Log("");
                    Log("ERROR: Installation failed!");
                    Log(ex.Message);
                    Log(ex.StackTrace);
                }
                finally
                {
                    // Enable Done button on UI thread
                    this.Invoke((MethodInvoker)delegate {
                        btnDone.Enabled = true;
                        btnDone.Visible = true;
                    });
                }
            });
        }

        // Drives the add-on install: optional overwrite confirmation, then runs the
        // copy + dlclist enable on a background thread, writing to the install log
        // exactly like the package path so the UX matches.
        private void InstallAddonPath()
        {
            var manager = new AddonManager(_gameFolder);
            if (!manager.ModsFolderExists)
            {
                MessageBox.Show(
                    "mods\\update\\update.rpf was not found in this game folder.\n\n" +
                    "Set up your mods folder (with OpenIV / RageOpenV) before installing add-ons.",
                    "Mods folder missing", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            bool overwrite = false;
            if (manager.AddonFolderExists(_addonName))
            {
                var r = MessageBox.Show(
                    $"An add-on folder named '{_addonName}' already exists at:\n\n" +
                    $"mods\\update\\x64\\dlcpacks\\{_addonName}\\\n\n" +
                    "Overwrite it with the dropped content?",
                    "Confirm overwrite",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                if (r != DialogResult.Yes) return;
                overwrite = true;
            }

            ShowInstallLog();

            string source = _addonSourcePath;
            string name = _addonName;

            Task.Run(() =>
            {
                try
                {
                    Log("Initializing add-on installation...");
                    Log($"Source     : {source}");
                    Log($"Destination: mods\\update\\x64\\dlcpacks\\{name}\\");
                    if (overwrite) Log("Existing folder will be replaced.");
                    Log("");

                    var progress = new Progress<string>(msg => Log(msg));
                    manager.InstallAddon(source, name, overwrite, progress);

                    Log("");
                    Log("Add-on installation completed successfully.");
                    Log("dlclist.xml updated inside mods\\update\\update.rpf.");
                    Log("----------------------------------------");
                }
                catch (Exception ex)
                {
                    Log("");
                    Log("ERROR: Add-on installation failed!");
                    Log(ex.Message);
                    if (ex.StackTrace != null) Log(ex.StackTrace);
                }
                finally
                {
                    this.Invoke((MethodInvoker)delegate
                    {
                        btnDone.Enabled = true;
                        btnDone.Visible = true;
                    });
                }
            });
        }

        private void btnDone_Click(object sender, EventArgs e)
        {
            ShowMainView();
        }
        
        private void ShowInstallLog()
        {
            // Reset the progress bar before the crossfade so the new view comes in clean.
            progressBar.Reset();
            rtbLog.Clear();
            btnDone.Enabled = false;
            btnDone.Visible = false;

            // Disable main buttons during install
            btnInstall.Enabled = false;
            btnUninstall.Enabled = false;

            // Crossfade panelContent's children: hide the main view, reveal the log view.
            ViewTransitions.CrossFade(panelContent, () =>
            {
                panelPaths.Visible = false;
                panelInfo.Visible = false;
                panelAdditional.Visible = false;
                rtbDescription.Visible = false;

                panelLog.Visible = true;
                panelLog.BringToFront();
            });
        }

        private void ShowMainView()
        {
            // Drop the loaded package and roll the form back to its empty-state landing.
            // After a Done click the user's natural next action is "install another mod"
            // or "close the app" — both are served by a clean slate. Keeping the picked
            // package visible would also make the now-enabled Install button ambiguous
            // (already installed? install again?).
            _package?.Dispose();
            _package = null;
            // Clear add-on mode if we came from a folder/dlc.rpf drop so the next
            // drop starts fresh (and Install routes through the package path again).
            _addonName = null;
            _addonSourcePath = null;
            txtOivPath.Text = "";
            this.Text = "CodeWalker - Package Installer";
            if (picIcon.Image != null)
            {
                var old = picIcon.Image;
                picIcon.Image = null;
                old.Dispose();
            }
            lblPackageName.Text = "Select Package";
            lblAuthor.Text = "";

            // Reset theme to default (the loaded package may have themed the header).
            Color defaultBlue = Color.FromArgb(0, 120, 215);
            panelHeader.BackColor = defaultBlue;
            lblPackageName.ForeColor = Color.White;
            lblAuthor.ForeColor = Color.White;
            lblWarning.ForeColor = Color.White;
            linkAuthor.LinkColor = defaultBlue;
            linkWeb.LinkColor = defaultBlue;
            linkYoutube.LinkColor = defaultBlue;
            btnInstall.ForeColor = Color.Black;
            btnInstall.FlatAppearance.BorderColor = Color.FromArgb(180, 180, 180);
            SyncMarqueeFromLabel();

            // Crossfade panelContent: hide log + loaded sections, reveal empty-state card.
            // panelPaths is restored to its empty-state Y and made visible again — it
            // was hidden by ShowInstallLog when the install began.
            ViewTransitions.CrossFade(panelContent, () =>
            {
                panelLog.Visible = false;
                panelPaths.Top = SY(160);
                panelPaths.Visible = true;
                panelEmptyState.Visible = true;
                rtbDescription.Visible = false;
                panelInfo.Visible = false;
                panelAdditional.Visible = false;
                picIcon.Visible = false;
                lblAuthor.Visible = false;
                linkInstructions.Visible = false;
            });

            // Run the inverse header/form animation in lockstep with the crossfade.
            AnimateLayoutTransitionToEmpty(durationMs: 280);

            // Restart the idle pulse now that we're back on the empty landing.
            _headerPulser?.Start();

            // Install is disabled in the empty state until a package is picked again.
            btnInstall.Enabled = false;
            btnUninstall.Enabled = true;
            ValidateGameFolder();
        }
        
        private void Log(string message)
        {
            if (rtbLog.InvokeRequired)
            {
                rtbLog.Invoke(new Action<string>(Log), message);
                return;
            }
            rtbLog.AppendText(message + Environment.NewLine);
            rtbLog.ScrollToCaret();
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _package?.Dispose();
            _marquee?.Dispose();
            _formFadeAnimator?.Dispose();
            _formResizeAnimator?.Dispose();
            _headerPulser?.Dispose();
            base.OnFormClosing(e);
        }
    }
}
