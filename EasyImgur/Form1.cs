﻿using EasyImgur.APIResponses;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

namespace EasyImgur
{
    public partial class Form1 : Form
    {
        private bool CloseCommandWasSentFromExitButton = false;
        private enum MessageVerbosity { Normal = 0, NoInfo = 1, NoError = 2 }
        private MessageVerbosity Verbosity = MessageVerbosity.Normal;

        /// <summary>
        /// Property to easily access the path of the executable, quoted for safety.
        /// </summary>
        private string QuotedApplicationPath { get { return "\"" + Application.ExecutablePath + "\""; } }

        public Form1(SingleInstance _SingleInstance, string[] _Args)
        {
            InitializeComponent();

            ImplementPortableMode();

            CreateHandle(); // force the handle to be created so Invoke succeeds; see issue #8 for more detail

            this.notifyIcon1.ContextMenu = this.trayMenu;

            this.versionLabel.Text = "Version " + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();

            Application.ApplicationExit += new System.EventHandler(this.ApplicationExit);

            InitializeEventHandlers();
            History.BindData(historyItemBindingSource); // to use the designer with data binding, we have to pass History our BindingSource, instead of just getting one from History
            History.InitializeFromDisk();

            // if we have arguments, we're going to show a tip when we handle those arguments. 
            if(_Args.Length == 0 && Properties.Settings.Default.showNotificationOnStartUp) 
                ShowBalloonTip(2000, "EasyImgur is ready for use!", "Right-click EasyImgur's icon in the tray to use it!", ToolTipIcon.Info);

            ImgurAPI.AttemptRefreshTokensFromDisk();

            Statistics.GatherAndSend();

            _SingleInstance.ArgumentsReceived += singleInstance_ArgumentsReceived;
            if(_Args.Length > 0) // handle initial arguments
                singleInstance_ArgumentsReceived(this, new ArgumentsReceivedEventArgs() { Args = _Args });
        }

        // Ensure that the clipboard settings container is enabled or disabled based on the master clipboard setting
        private void SyncClipboardSettingsContainerEnabledState()
        {
            this.clipboardSettingsContainer.Enabled = this.checkBoxCopyLinks.Checked;
        }

        private void InitializeEventHandlers()
        {
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.Form1_Closing);
            this.Load += new System.EventHandler(this.Form1_Load);
            notifyIcon1.BalloonTipClicked += new System.EventHandler(this.NotifyIcon1_BalloonTipClicked);
            notifyIcon1.MouseDoubleClick += new System.Windows.Forms.MouseEventHandler(this.NotifyIcon1_MouseDoubleClick);
            tabControl1.SelectedIndexChanged += new System.EventHandler(this.tabControl1_SelectedIndexChanged);

            ImgurAPI.obtainedAuthorization += new ImgurAPI.AuthorizationEventHandler(this.ObtainedAPIAuthorization);
            ImgurAPI.refreshedAuthorization += new ImgurAPI.AuthorizationEventHandler(this.RefreshedAPIAuthorization);
            ImgurAPI.lostAuthorization += new ImgurAPI.AuthorizationEventHandler(this.LostAPIAuthorization);
            ImgurAPI.networkRequestFailed += new ImgurAPI.NetworkEventHandler(this.APINetworkRequestFailed);
        }

        void ImplementPortableMode()
        {
            if (Program.InPortableMode)
            {
                this.checkBoxLaunchAtBoot.Enabled = false;
                this.checkBoxEnableContextMenu.Enabled = false;
                this.Text += " - Portable Mode";
            }
            else
            {
                this.labelPortableModeNote.Visible = false;
            }
        }

        bool ShouldShowMessage(MessageVerbosity _Verbosity)
        {
            return Verbosity < _Verbosity;
        }

        void singleInstance_ArgumentsReceived( object sender, ArgumentsReceivedEventArgs e )
        {
            // Using "/exit" anywhere in the command list will cause EasyImgur to exit after uploading;
            // this will happen regardless of the execution sending the exit command was the execution that
            // launched the initial instance of EasyImgur.
            bool exitWhenFinished = false;
            bool anonymous = false;

            // mappings of switch names to actions
            Dictionary<string, Action> handlers = new Dictionary<string, Action>() {
                { "anonymous", () => anonymous = true },
                { "noinfo", () => Verbosity = (MessageVerbosity)Math.Max((int)Verbosity, (int)MessageVerbosity.NoInfo) },
                { "q", () => Verbosity = (MessageVerbosity)Math.Max((int)Verbosity, (int)MessageVerbosity.NoInfo) },
                { "noerr", () => Verbosity = (MessageVerbosity)Math.Max((int)Verbosity, (int)MessageVerbosity.NoError) },
                { "qq", () => Verbosity = (MessageVerbosity)Math.Max((int)Verbosity, (int)MessageVerbosity.NoError) },
                { "exit", () => exitWhenFinished = true },
                { "portable", () => { } } // ignore
            };
            
            try
            {
                // First scan for switches
                int badSwitchCount = 0;
                foreach (String str in e.Args.Where(s => s != null && s.StartsWith("/")))
                {
                    String param = str.Remove(0, 1); // Strip the leading '/' from the switch.

                    if (handlers.ContainsKey(param))
                    {
                        Log.Warning("Consuming command-line switch '" + param + "'.");
                        handlers[param]();
                    }
                    else
                    {
                        ++badSwitchCount;
                        Log.Warning("Ignoring unrecognized command-line switch '" + param + "'.");
                    }
                }

                if (badSwitchCount > 0 && ShouldShowMessage(MessageVerbosity.NoError))
                {
                    ShowBalloonTip(2000, "Invalid switch", badSwitchCount.ToString() + " invalid switch" + (badSwitchCount > 1 ? "es were" : " was") + " passed to EasyImgur (see log for details). No files were uploaded.", ToolTipIcon.Error, true);
                    return;
                }

                // Process actual arguments
                foreach(string path in e.Args.Where(s => s != null && !s.StartsWith("/")))
                {
                    if(!anonymous && !ImgurAPI.HasBeenAuthorized())
                    {
                        ShowBalloonTip(2000, "Not logged in", "You aren't logged in but you're trying to upload to an account. Authorize EasyImgur and try again.", ToolTipIcon.Error, true);
                        return;
                    }

                    if(Directory.Exists(path))
                    {
                        string[] fileTypes = new[] { ".jpg", ".jpeg", ".png", ".apng", ".bmp",
                            ".gif", ".tiff", ".tif", ".xcf" , ".webp" };
                        List<string> files = new List<string>();
                        foreach (string s in Directory.GetFiles(path))
                        {
                            bool cont = false;
                            foreach (string filetype in fileTypes)
                                if (s.EndsWith(filetype, true, null))
                                {
                                    cont = true;
                                    break;
                                }
                            if (!cont)
                                continue;

                            files.Add(s);
                        }

                        UploadAlbum(anonymous, files.ToArray(), path.Split('\\').Last());
                    }
                    else if(File.Exists(path))
                        UploadFile(anonymous, new string[] { path });
                }
            }
            catch(Exception ex)
            {
                Log.Error("Unhandled exception in context menu thread: " + ex.ToString());
                ShowBalloonTip(2000, "Error", "An unknown exception occurred during upload. Check the log for further information.", ToolTipIcon.Error, true);
            }

            if(exitWhenFinished)
                Application.Exit();

            // reset verbosity
            Verbosity = MessageVerbosity.Normal;
        }

        private void ShowBalloonTip( int _Timeout, string _Title, string _Text, ToolTipIcon _Icon, bool error = false )
        {
            if (ShouldShowMessage(error ? MessageVerbosity.NoError : MessageVerbosity.NoInfo))
            {
                if (notifyIcon1 != null)
                    notifyIcon1.ShowBalloonTip(_Timeout, _Title, _Text, _Icon);
                Log.Info(string.Format("Showed tooltip with title \"{0}\" and text \"{1}\".", _Title, _Text));
            }
            else
            {
                Log.Info(string.Format("Tooltip with title \"{0}\" and text \"{1}\" was suppressed.", _Title, _Text));
            }
        }

        private void ApplicationExit( object sender, EventArgs e )
        {
            Statistics.GatherAndSend();

            ImgurAPI.OnMainThreadExit();
            if (notifyIcon1 != null)
                notifyIcon1.Visible = false;
        }

        private void ObtainedAPIAuthorization()
        {
            SetAuthorizationStatusUI(true);
            ShowBalloonTip(2000, "EasyImgur", "EasyImgur has received authorization to use your Imgur account!", ToolTipIcon.Info);
        }

        private void RefreshedAPIAuthorization()
        {
            SetAuthorizationStatusUI(true);
            if (Properties.Settings.Default.showNotificationOnTokenRefresh)
                ShowBalloonTip(2000, "EasyImgur", "EasyImgur has successfully refreshed authorization tokens!", ToolTipIcon.Info);
        }

        private void SetAuthorizationStatusUI( bool _IsAuthorized )
        {
            uploadClipboardAccountTrayMenuItem.Enabled = _IsAuthorized;
            uploadFileAccountTrayMenuItem.Enabled = _IsAuthorized;
            label13.Text = _IsAuthorized ? "Authorized" : "Not authorized";
            label13.ForeColor = _IsAuthorized ? System.Drawing.Color.Green : System.Drawing.Color.DarkBlue;
            buttonForceTokenRefresh.Enabled = _IsAuthorized;
            buttonForgetTokens.Enabled = _IsAuthorized;
        }

        private void LostAPIAuthorization()
        {
            SetAuthorizationStatusUI(false);
            ShowBalloonTip(2000, "EasyImgur", "EasyImgur no longer has authorization to use your Imgur account!", ToolTipIcon.Info);
        }

        private void APINetworkRequestFailed()
        {
            ShowBalloonTip(2000, "EasyImgur", "Network request failed. Check your internet connection.", ToolTipIcon.Error, true);
        }

        private void tabControl1_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (tabControl1.SelectedIndex == 2)
            {
                SelectedHistoryItemChanged();
            }
        }

        // Resize @a _Image to an image that fits in [_MaxWidth, _MaxHeight], with the original aspect ratio retained
        private Image GenerateThumbnailImage(Image _Image, int _MaxWidth, int _MaxHeight)
        {
            int original_width = _Image.Width;
            int original_height = _Image.Height;

            float scale_factor = 1.0f;

            if (_MaxWidth < _MaxHeight)
                scale_factor = (float)_MaxWidth / original_width;  
            else
                scale_factor = (float)_MaxHeight / original_height;

            // Only need to generate a new image if the old one is too large to serve as a thumbnail as-is
            if (scale_factor >= 1.0f)
                return _Image;

            int new_width = Math.Max((int)((float)original_width * scale_factor), 1);
            int new_height = Math.Max((int)((float)original_height * scale_factor), 1);

            return _Image.GetThumbnailImage(new_width, new_height, null, System.IntPtr.Zero);
        }

        private void ClipboardSetLink(string link)
        {
            link = Properties.Settings.Default.copyHttpsLinks
                      ? link.Replace("http://", "https://")
                      : link;
            if (Properties.Settings.Default.copyIMGTag)
                link = "[IMG]" + link + "[/IMG]";
            Clipboard.SetText(link);
        }

        private void UploadClipboard( bool _Anonymous )
        {
            APIResponses.ImageResponse resp = null;
            Image clipboardImage = null;
            string clipboardURL = string.Empty;
            //bool anonymous = !Properties.Settings.Default.useAccount || !ImgurAPI.HasBeenAuthorized();
            bool anonymous = _Anonymous;
            if (Clipboard.ContainsImage())
            {
                clipboardImage = Clipboard.GetImage();
                ShowBalloonTip(4000, "Hold on...", "Attempting to upload image to Imgur...", ToolTipIcon.None);
                resp = ImgurAPI.UploadImage(clipboardImage, GetTitleString(null), GetDescriptionString(null), _Anonymous);
            }
            else if (Clipboard.ContainsText())
            {
                clipboardURL = Clipboard.GetText(TextDataFormat.UnicodeText);
                Uri uriResult;
                if (Uri.TryCreate(clipboardURL, UriKind.Absolute, out uriResult) && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps))
                {
                    ShowBalloonTip(4000, "Hold on...", "Attempting to upload image to Imgur...", ToolTipIcon.None);
                    resp = ImgurAPI.UploadImage(clipboardURL, GetTitleString(null), GetDescriptionString(null), _Anonymous);
                }
                else
                {
                    ShowBalloonTip(2000, "Can't upload clipboard!", "There's text on the clipboard but it's not a valid URL", ToolTipIcon.Error, true);
                    return;
                }
            }
            else
            {
                ShowBalloonTip(2000, "Can't upload clipboard!", "There's no image or URL there", ToolTipIcon.Error, true);
                return;
            }
            if (resp.Success)
            {
                // this doesn't need an invocation guard because this function can't be called from the context menu
                if (Properties.Settings.Default.copyLinks)
                {
                    ClipboardSetLink(resp.ResponseData.Link);
                }

                ShowBalloonTip(2000, "Success!", Properties.Settings.Default.copyLinks ? "Link copied to clipboard" : "Upload placed in history: " + resp.ResponseData.Link, ToolTipIcon.None);

                HistoryItem item = new HistoryItem();
                item.Timestamp = DateTime.Now;
                item.Id = resp.ResponseData.Id;
                item.Link = resp.ResponseData.Link;
                item.Deletehash = resp.ResponseData.DeleteHash;
                item.Title = resp.ResponseData.Title;
                item.Description = resp.ResponseData.Description;
                item.Anonymous = anonymous;
                item.Width = resp.ResponseData.Width;
                item.Height = resp.ResponseData.Height;
                if (clipboardImage != null)
                    item.Thumbnail = GenerateThumbnailImage(clipboardImage, pictureBoxHistoryThumb.Width, pictureBoxHistoryThumb.Height);
                History.StoreHistoryItem(item);
            }
            else
                ShowBalloonTip(2000, "Failed", "Could not upload image (" + resp.Status + "):", ToolTipIcon.None, true);

            if (!Properties.Settings.Default.clearClipboardOnUpload)
            {
                if (clipboardImage != null)
                    Clipboard.SetImage(clipboardImage);
                else
                    Clipboard.SetText(clipboardURL, TextDataFormat.UnicodeText);
            }

            Statistics.GatherAndSend();
        }

        private void UploadAlbum( bool _Anonymous, string[] _Paths, string _AlbumTitle )
        {
            ShowBalloonTip(2000, "Hold on...", "Attempting to upload album to Imgur (this may take a while)...", ToolTipIcon.None);
            
            // Create the album
            APIResponses.AlbumResponse album_response = ImgurAPI.CreateAlbum(_AlbumTitle, _Anonymous);
            if (!album_response.Success)
            {
                ShowBalloonTip(2000, "Failed", "Could not create album (" + album_response.Status + "): " + album_response.ResponseData.Error.Message, ToolTipIcon.None, true);
                Statistics.GatherAndSend();
                return;
            }

            // If we did manage to create the album we can now try to add images to it
            int succeeded_count = 0;
            int failed_count = 0;
            int image_idx = 0;
            Image album_thumbnail = null;

            foreach (string path in _Paths)
            {
                // Instead of loading every file into memory, peek only their header to check if they are valid images
                try
                {
                    Image img = Image.FromStream(new MemoryStream(File.ReadAllBytes(path)));
                    
                    string title = string.Empty;
                    string description = string.Empty;

                    FormattingHelper.FormattingContext format_context = new FormattingHelper.FormattingContext();
                    format_context.FilePath = path;
                    format_context.AlbumIndex = ++image_idx;

                    APIResponses.ImageResponse resp = ImgurAPI.UploadImage(img, GetTitleString(format_context), GetDescriptionString(format_context), _Anonymous, ref album_response);

                    // If we haven't selected any thumbnail yet, or if the currently uploaded image has been turned into the cover image of the album, turn it into a thumbnail for the history view.
                    if (album_thumbnail == null || resp.ResponseData.Id == album_response.ResponseData.Cover)
                        album_thumbnail = GenerateThumbnailImage(img, pictureBoxHistoryThumb.Width, pictureBoxHistoryThumb.Height);

                    if (resp.Success)
                        succeeded_count++;
                    else
                        failed_count++;
                }
                catch (ArgumentException)
                {
                    ShowBalloonTip(2000, "Failed", "File (" + path + ") is not a valid image file.", ToolTipIcon.Error, true);
                }
                catch(FileNotFoundException)
                {
                    ShowBalloonTip(2000, "Failed", "Could not find image file on disk (" + path + "):", ToolTipIcon.Error, true);
                }
                catch(IOException)
                {
                    ShowBalloonTip(2000, "Failed", "Image is in use by another program (" + path + "):", ToolTipIcon.Error, true);
                }
            }

            // If no images managed to upload to the album we should delete it again
            if (failed_count == _Paths.Count())
            {
                ShowBalloonTip(2000, "Album upload cancelled", "All images failed to upload! Check the log for more info.", ToolTipIcon.Error, true);

                // Delete the album because we couldn't upload anything
                ImgurAPI.DeleteAlbum(album_response.ResponseData.DeleteHash, _Anonymous);

                Statistics.GatherAndSend();
                return;
            }
            
            // Did we succeed in uploading the album?
            if (album_response.Success)
            {
                // Clipboard calls can only be made on an STA thread, threading model is MTA when invoked from context menu
                if (System.Threading.Thread.CurrentThread.GetApartmentState() != System.Threading.ApartmentState.STA)
                {
                    this.Invoke(new Action(() =>
                    ClipboardSetLink(album_response.ResponseData.Link)));
                }
                else
                {
                    ClipboardSetLink(album_response.ResponseData.Link);
                }
                
                ShowBalloonTip(2000, "Success!", Properties.Settings.Default.copyLinks ? "Link copied to clipboard" : "Upload placed in history: " + album_response.ResponseData.Link, ToolTipIcon.None);

                HistoryItem item = new HistoryItem();
                item.Timestamp = DateTime.Now;
                item.Id = album_response.ResponseData.Id;
                item.Link = album_response.ResponseData.Link;
                item.Deletehash = album_response.ResponseData.DeleteHash;
                item.Title = album_response.ResponseData.Title;
                item.Description = album_response.ResponseData.Description;
                item.Anonymous = _Anonymous;
                item.Album = true;
                item.Thumbnail = album_thumbnail;
                Invoke(new Action(() => History.StoreHistoryItem(item)));
            }
            else
                ShowBalloonTip(2000, "Failed", "Could not upload album (" + album_response.Status + "): " + album_response.ResponseData.Error.Message, ToolTipIcon.None, true);

            Statistics.GatherAndSend();
        }

        private void UploadFile( bool _Anonymous, string[] _Paths = null )
        {
            if(_Paths == null)
            {
                System.Windows.Forms.OpenFileDialog dialog = new System.Windows.Forms.OpenFileDialog();
                dialog.Multiselect = true;
                DialogResult res = dialog.ShowDialog();
                if(res == DialogResult.OK)
                    _Paths = dialog.FileNames;
            }
            if (_Paths != null)
            {
                if(_Paths.Length > 1 && Properties.Settings.Default.uploadMultipleImagesAsGallery)
                {
                    UploadAlbum(_Anonymous, _Paths, "");
                    return;
                }

                int success = 0;
                int failure = 0;
                int i = 0;
                foreach (string fileName in _Paths)
                {
                    ++i;

                    if (fileName == null || fileName == string.Empty)
                        continue;

                    string fileCounterString = (_Paths.Length > 1) ? (" (" + i.ToString() + "/" + _Paths.Length.ToString() + ") ") : (string.Empty);

                    try
                    {
                        ShowBalloonTip(2000, "Hold on..." + fileCounterString, "Attempting to upload image to Imgur...", ToolTipIcon.None);
                        Image img;
                        APIResponses.ImageResponse resp;
                        using(System.IO.FileStream stream = System.IO.File.Open(fileName, System.IO.FileMode.Open))
                        {
                            // a note to the future: ImgurAPI.UploadImage called Image.Save(); Image.Save()
                            // requires that the stream it was loaded from still be open, or else you get
                            // an immensely generic error. 
                            img = System.Drawing.Image.FromStream(stream);
                            FormattingHelper.FormattingContext format_context = new FormattingHelper.FormattingContext();
                            format_context.FilePath = fileName;
                            resp = ImgurAPI.UploadImage(img, GetTitleString(format_context), GetDescriptionString(format_context), _Anonymous);
                        }
                        if (resp.Success)
                        {
                            success++;

                            if (Properties.Settings.Default.copyLinks)
                            {
                                // clipboard calls can only be made on an STA thread, threading model is MTA when invoked from context menu
                                if (System.Threading.Thread.CurrentThread.GetApartmentState() !=
                                    System.Threading.ApartmentState.STA)
                                {
                                    this.Invoke(new Action(() =>
                                        ClipboardSetLink(resp.ResponseData.Link)));
                                }
                                else
                                {
                                    ClipboardSetLink(resp.ResponseData.Link);
                                }
                            }

                            ShowBalloonTip(2000, "Success!" + fileCounterString, Properties.Settings.Default.copyLinks ? "Link copied to clipboard" : "Upload placed in history: " + resp.ResponseData.Link, ToolTipIcon.None);

                            HistoryItem item = new HistoryItem(); 
                            item.Timestamp = DateTime.Now;
                            item.Id = resp.ResponseData.Id;
                            item.Link = resp.ResponseData.Link;
                            item.Deletehash = resp.ResponseData.DeleteHash;
                            item.Title = resp.ResponseData.Title;
                            item.Description = resp.ResponseData.Description;
                            item.Anonymous = _Anonymous;
                            item.Width = resp.ResponseData.Width;
                            item.Height = resp.ResponseData.Height;
                            item.Thumbnail = GenerateThumbnailImage(img, pictureBoxHistoryThumb.Width, pictureBoxHistoryThumb.Height);
                            Invoke(new Action(() => History.StoreHistoryItem(item)));
                        }
                        else
                        {
                            failure++;
                            ShowBalloonTip(2000, "Failed" + fileCounterString, "Could not upload image (" + resp.Status + "): " + resp.ResponseData.Error.Message, ToolTipIcon.None, true);
                        }
                    }
                    catch (ArgumentException)
                    {
                        ShowBalloonTip(2000, "Failed" + fileCounterString, "File (" + fileName + ") is not a valid image file.", ToolTipIcon.Error, true);
                    }
                    catch (System.IO.FileNotFoundException)
                    {
                        failure++;
                        ShowBalloonTip(2000, "Failed" + fileCounterString, "Could not find image file on disk (" + fileName + "):", ToolTipIcon.Error, true);
                    }
                    catch(IOException)
                    {
                        ShowBalloonTip(2000, "Failed" + fileCounterString, "Image is in use by another program (" + fileName + "):", ToolTipIcon.Error, true);
                    }
                }
                if(_Paths.Length > 1)
                {
                    ShowBalloonTip(2000, "Done", "Successfully uploaded " + success.ToString() + " files" + ((failure > 0) ? (" (Warning: " + failure.ToString() + " failed)") : (string.Empty)), ToolTipIcon.Info, failure > 0);
                }
            }

            Statistics.GatherAndSend();
        }

        private void buttonOK_Click(object sender, EventArgs e)
        {
            SaveSettings();
            //this.Hide();
        }

        private void NotifyIcon1_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            this.Show();
            this.Activate();
            this.Focus();
            this.BringToFront();
        }

        private void NotifyIcon1_BalloonTipClicked(object sender, EventArgs e)
        {
            /*if (!Properties.Settings.Default.copyLinks)
            {

            }*/
            this.Show();
            tabControl1.SelectedIndex = 2;
            listBoxHistory.SelectedIndex = listBoxHistory.Items.Count - 1;
            this.BringToFront();
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            Properties.Settings.Default.Reload();

            // Assign control values. Most values are set using application binding on the control.
            comboBoxImageFormat.SelectedIndex = Properties.Settings.Default.imageFormat;
            SyncClipboardSettingsContainerEnabledState();

            // Check the registry for a key describing whether EasyImgur should be started on boot.
            Microsoft.Win32.RegistryKey registryKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
            string value = (string)registryKey.GetValue("EasyImgur", string.Empty); // string.Empty is returned if no key is present.
            checkBoxLaunchAtBoot.Checked = value != string.Empty;
            if(value != string.Empty && value != QuotedApplicationPath)
            {
                // A key exists, make sure we're using the most up-to-date path!
                registryKey.SetValue("EasyImgur", QuotedApplicationPath);
                ShowBalloonTip(2000, "EasyImgur", "Updated registry path", ToolTipIcon.Info);
            }
            UpdateRegistry(true); // this will need to be updated too, if we're using it

            // Bind the data source for the list of contributors.
            Contributors.BindingSource.DataSource = Contributors.ContributorList;
            contributorsList.DataSource = Contributors.BindingSource;
        }

        private void Form1_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            SaveSettings();
            this.Hide();

            e.Cancel = !CloseCommandWasSentFromExitButton;  // Don't want to *actually* close the form unless the Exit button was used.
        }

        private void SaveSettings()
        {
            // Store control values. Most values are stored using application binding on the control.
            Properties.Settings.Default.imageFormat = comboBoxImageFormat.SelectedIndex;

            using(Microsoft.Win32.RegistryKey registryKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true))
            {
                if (checkBoxLaunchAtBoot.Checked)
                {
                    // If the checkbox was marked, set a value which will make EasyImgur start at boot.
                    registryKey.SetValue("EasyImgur", QuotedApplicationPath);
                }
                else
                {
                    // Delete our value if one is present; second argument supresses exception on missing value
                    registryKey.DeleteValue("EasyImgur", false);
                }
            }

            UpdateRegistry(false);

            Properties.Settings.Default.Save();
        }

        private void UpdateRegistry(bool _LoadingSettings)
        {
            if(_LoadingSettings) // update enableContextMenu if necessary
            {
                // look for one of our handlers; Directory is the easiest and most reliable
                // Since HKCR is a merging of HKCU\Software\Classes and HKLM\Software\Classes, look there
                using(RegistryKey dir = Registry.ClassesRoot.OpenSubKey("Directory"))
                using(RegistryKey shell = dir.OpenSubKey("shell"))
                    Properties.Settings.Default.enableContextMenu = shell != null && shell.OpenSubKey("imguruploadanonymous") != null;
            }

            // a note: Directory doesn't work if within SystemFileAssociations, and 
            // the extensions don't work if not inside them. At least, this seems to be the case for me

            // another note: I discovered that I had the logic flipped, and the code actually did the opposite
            // of what I describe in the above note, and it was working. Earlier, though, when I wrote that,
            // it seemed to be true. Either way, the placement (inside or outside of SystemFileAssociations)
            // does affect where in the context menu they show up. Feel free to play with the placement and see
            // if you can get it to work better.
            string[] fileTypes = new[] { ".jpg", ".jpeg", ".png", ".apng", ".bmp",
            ".gif", ".tiff", ".tif", ".pdf", ".xcf", ".webp", "Directory" };
            using(RegistryKey root = Registry.CurrentUser.OpenSubKey("Software\\Classes", true))
            using(RegistryKey fileAssoc = root.CreateSubKey("SystemFileAssociations"))
                foreach(string fileType in fileTypes)
                    using(RegistryKey fileTypeKey = (fileType != "Directory" ? fileAssoc.CreateSubKey(fileType) : root.CreateSubKey(fileType)))
                    using(RegistryKey shell = fileTypeKey.CreateSubKey("shell"))
                    {
                        if(Properties.Settings.Default.enableContextMenu)
                        {
                            using(RegistryKey anonHandler = shell.CreateSubKey("imguruploadanonymous"))
                                EnableContextMenu(anonHandler, "Upload to Imgur" +
                                    (fileType == "Directory" ? " as album" : "") + " (anonymous)", true);
                            using(RegistryKey accHandler = shell.CreateSubKey("imgurupload"))
                                EnableContextMenu(accHandler, "Upload to Imgur" +
                                    (fileType == "Directory" ? " as album" : ""), false);
                        }
                        else
                        {
                            shell.DeleteSubKeyTree("imguruploadanonymous", false);
                            shell.DeleteSubKeyTree("imgurupload", false);
                        }
                    }
        }

        private void EnableContextMenu(RegistryKey key, string commandText, bool anonymous)
        {
            key.SetValue("", commandText);
            key.SetValue("Icon", QuotedApplicationPath);
            using(RegistryKey subKey = key.CreateSubKey("command"))
                subKey.SetValue("", QuotedApplicationPath + (anonymous ? " /anonymous" : "") + " \"%1\"");
        }

        private void buttonChangeCredentials_Click(object sender, EventArgs e)
        {
            AuthorizeForm accountCredentialsForm = new AuthorizeForm();
            DialogResult res = accountCredentialsForm.ShowDialog(this);
            
            if (ImgurAPI.HasBeenAuthorized())
            {
                buttonForceTokenRefresh.Enabled = true;
            }
            else
            {
                buttonForceTokenRefresh.Enabled = false;
            }
        }

        private void SelectedHistoryItemChanged()
        {
            groupBoxHistorySelection.Text =
                String.Format("Selection: {0} {1}", listBoxHistory.SelectedItems.Count, listBoxHistory.SelectedItems.Count == 1 ? "item" : "items");
            HistoryItem item = listBoxHistory.SelectedItem as HistoryItem;
            if (item == null)
            {
                buttonRemoveFromImgur.Enabled = false;
                buttonRemoveFromHistory.Enabled = false;
                btnOpenImageLinkInBrowser.Enabled = false;
            }
            else
            {
                buttonRemoveFromImgur.Enabled = item.Anonymous || (!item.Anonymous && ImgurAPI.HasBeenAuthorized());
                buttonRemoveFromHistory.Enabled = true;
                btnOpenImageLinkInBrowser.Enabled = true;
            }
        }

        private void listBoxHistory_SelectedIndexChanged(object sender, EventArgs e)
        {
            SelectedHistoryItemChanged();
        }

        private string FormatInfoString( string _Input, FormattingHelper.FormattingContext _FormattingContext )
        {
            return FormattingHelper.Format(_Input, _FormattingContext);
        }

        private string GetTitleString( FormattingHelper.FormattingContext _FormattingContext )
        {
            return FormatInfoString(textBoxTitleFormat.Text, _FormattingContext);
        }

        private string GetDescriptionString(FormattingHelper.FormattingContext _FormattingContext)
        {
            return FormatInfoString(textBoxDescriptionFormat.Text, _FormattingContext);
        }

        private SortedSet<int> getSelectedRowIndicesFromHistoryGridView()
        {
            SortedSet<int> unique_row_indices = new SortedSet<int>();
            
            // Generate a sorted set of unique row indices. This accomplishes multiple goals:
            // - We want the set to be sorted
            // - We want to only output a given row index once, even if multiple cells are selected for a given row/column (DataGridView.SelectedRows and DataGridView.SelectedColumns are empty unless an entire row/column is selected)
            foreach (DataGridViewCell cell in dataGridViewHistory.SelectedCells)
                unique_row_indices.Add(cell.RowIndex);

            return unique_row_indices;
        }
        private SortedSet<int> getSelectedColumnIndicesFromHistoryGridView()
        {
            SortedSet<int> unique_col_indices = new SortedSet<int>();
            
            // Generate a sorted set of unique column indices. This accomplishes multiple goals:
            // - We want the set to be sorted
            // - We want to only output a given column index once, even if multiple cells are selected for a given row/column (DataGridView.SelectedRows and DataGridView.SelectedColumns are empty unless an entire row/column is selected)
            foreach (DataGridViewCell cell in dataGridViewHistory.SelectedCells)
                unique_col_indices.Add(cell.ColumnIndex);

            return unique_col_indices;
        }


        // Get a list of HistoryItems that are currently considered to be selected in the history gridview
        // This method works even if only partial rows are selected. The returned list is always sorted
        // based on the row index of each item in the history grid, and contains no null entries.
        private List<HistoryItem> getSelectedHistoryItemsFromHistoryGridView()
        {
            List<HistoryItem> selected_items = new List<HistoryItem>();
            foreach (int row_index in getSelectedRowIndicesFromHistoryGridView())
                if (dataGridViewHistory.Rows[row_index].DataBoundItem is HistoryItem item)
                    selected_items.Add(item);

            return selected_items;
        }

        private void removeItemsFromImgur(List<HistoryItem> itemsToRemove)
        {
            int count = itemsToRemove.Count;
            bool isMultipleImages = count > 1;
            int currentCount = 0;

            listBoxHistory.BeginUpdate();
            dataGridViewHistory.Enabled = false;

            foreach(HistoryItem item in itemsToRemove)
            {
                ++currentCount;

                if (item == null)
                    return;

                string balloon_image_counter_text = (isMultipleImages ? (currentCount.ToString() + "/" + count.ToString()) : string.Empty);
                string balloon_text = "Attempting to remove " + (item.Album ? "album" : "image") + " " + balloon_image_counter_text + " from Imgur...";
                ShowBalloonTip(2000, "Hold on...", balloon_text, ToolTipIcon.None);
                if (item.Album ? ImgurAPI.DeleteAlbum(item.Deletehash, item.Anonymous) : ImgurAPI.DeleteImage(item.Deletehash, item.Anonymous))
                {
                    ShowBalloonTip(2000, "Success!", "Removed " + (item.Album ? "album" : "image") + " " + balloon_image_counter_text + " from Imgur and history", ToolTipIcon.None);
                    History.RemoveHistoryItem(item);
                }
                else
                    ShowBalloonTip(2000, "Failed", "Failed to remove " + (item.Album ? "album" : "image") + " " + balloon_image_counter_text + " from Imgur", ToolTipIcon.Error);
            }
            
            dataGridViewHistory.Enabled = true;
            listBoxHistory.EndUpdate();

            Statistics.GatherAndSend();
        }

        private void buttonRemoveFromImgur_Click(object sender, EventArgs e)
        {            
            List<HistoryItem> selectedItems = new List<HistoryItem>(listBoxHistory.SelectedItems.Cast<HistoryItem>());
            removeItemsFromImgur(selectedItems);
        }

        private void buttonForceTokenRefresh_Click(object sender, EventArgs e)
        {
            ImgurAPI.ForceRefreshTokens();
            SetAuthorizationStatusUI(ImgurAPI.HasBeenAuthorized());
        }

        private void buttonFormatHelp_Click(object sender, EventArgs e)
        {
            var sb = new StringBuilder(
                "You can use strings consisting of either static characters or " +
                "the following dynamic symbols, or a combination of both:\n\n");
            foreach (FormattingHelper.FormattingScheme scheme in FormattingHelper.GetSchemes())
            {
                sb.Append(scheme.Symbol);
                sb.Append("  :  ");
                sb.Append(scheme.Description);
                sb.Append('\n');
            }

            const string exampleFormattedString = "Image_%date%_%time%";

            sb.Append("\n\nEx.: '");
            sb.Append(exampleFormattedString);
            sb.Append("' would become: '");
            sb.Append(FormattingHelper.Format(exampleFormattedString, null));

            Point loc = this.Location;
            loc.Offset(buttonFormatHelp.Location.X, buttonFormatHelp.Location.Y);
            Help.ShowPopup(this, sb.ToString(), loc);
        }

        private void buttonForgetTokens_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show("Are you sure you want to discard the tokens?\n\nWithout tokens, the app can no longer use your Imgur account.", "Forget tokens", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                ImgurAPI.ForgetTokens();
                MessageBox.Show("Tokens have been forgotten. Remember that the app has still technically been authorized on the Imgur website, we can't change this for you!\n\nGo to http://imgur.com/account/settings/apps to revoke access.", "Tokens discarded", MessageBoxButtons.OK);
            }
        }

        private void buttonRemoveFromHistory_Click(object sender, EventArgs e)
        {
            listBoxHistory.BeginUpdate();
            List<HistoryItem> selectedItems = new List<HistoryItem>(listBoxHistory.SelectedItems.Cast<HistoryItem>());
            foreach(HistoryItem item in selectedItems)
            {
                if (item == null)
                {
                    return;
                }

                History.RemoveHistoryItem(item);
            }
            listBoxHistory.EndUpdate();
        }

        private void uploadClipboardAccountTrayMenuItem_Click(object sender, EventArgs e)
        {
            UploadClipboard(false);
        }

        private void uploadFileAccountTrayMenuItem_Click(object sender, EventArgs e)
        {
            UploadFile(false);
        }

        private void uploadClipboardAnonymousTrayMenuItem_Click(object sender, EventArgs e)
        {
            UploadClipboard(true);
        }

        private void uploadFileAnonymousTrayMenuItem_Click(object sender, EventArgs e)
        {
            UploadFile(true);
        }

        private void settingsTrayMenuItem_Click(object sender, EventArgs e)
        {
            // Open settings form.
            this.Show();
        }

        private void exitTrayMenuItem_Click(object sender, EventArgs e)
        {
            CloseCommandWasSentFromExitButton = true;
            Application.Exit();
        }

        private void linkLabel2_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            System.Diagnostics.Process.Start("https://easyimgur.bryankeiren.com/");
        }

        private void linkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            System.Diagnostics.Process.Start("https://bryankeiren.com/");
        }

        private void linkLabel3_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            System.Diagnostics.Process.Start("https://imgur.com/account/settings/apps");
        }

        private void btnOpenImageLinkInBrowser_Click(object sender, EventArgs e)
        {
            foreach (HistoryItem item in listBoxHistory.SelectedItems)
            {
                if (item == null)
                    continue;
            
                System.Diagnostics.Process.Start(item.Link);
            }
        }

        private void checkBoxCopyLinks_CheckedChanged(object sender, EventArgs e)
        {
            SyncClipboardSettingsContainerEnabledState();
        }

        private void buttonOpenLogFolder_Click(object sender, EventArgs e)
        {
            System.Diagnostics.Process.Start(Log.SaveLocation);
        }

        private void buttonViewLog_Click(object sender, EventArgs e)
        {
            System.Diagnostics.Process.Start(Log.LogFile);
        }

        private void dataGridViewHistory_CellContentDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            // If a link cell or the thumbnail cell double-clicked, open the link in a browser window
            if (e.ColumnIndex == linkDataGridViewTextBoxColumn.Index || 
                e.ColumnIndex == thumbnailDataGridViewImageColumn.Index)
            {
                if (dataGridViewHistory.Rows[e.RowIndex].DataBoundItem is HistoryItem item)
                    System.Diagnostics.Process.Start(item.Link);
            }
        }

        private void toolStripButtonHistoryGridCopy_Click(object sender, EventArgs e)
        {
            // Copy the contents of all selected cells
            Clipboard.SetDataObject(dataGridViewHistory.GetClipboardContent());
        }

        private void toolStripButtonHistoryGridOpenLink_Click(object sender, EventArgs e)
        {
            foreach (HistoryItem item in getSelectedHistoryItemsFromHistoryGridView())
                    System.Diagnostics.Process.Start(item.Link);
        }

        private void toolStripButtonHistoryGridSelectAll_Click(object sender, EventArgs e)
        {            
            dataGridViewHistory.SelectAll();
        }

        private void toolStripButtonHistoryGridSelectColumn_Click(object sender, EventArgs e)
        {
            // Cache the selected rows before changing the selection mode, because that action will reset the selection.
            SortedSet<int> selected_cols = getSelectedColumnIndicesFromHistoryGridView();

            dataGridViewHistory.SelectionMode = DataGridViewSelectionMode.ColumnHeaderSelect;

            foreach (int col_index in selected_cols)
                dataGridViewHistory.Columns[col_index].Selected = true;
        }

        private void toolStripButtonHistoryGridSelectRow_Click(object sender, EventArgs e)
        {
            // Cache the selected rows before changing the selection mode, because that action will reset the selection.
            SortedSet<int> selected_rows = getSelectedRowIndicesFromHistoryGridView();

            dataGridViewHistory.SelectionMode = DataGridViewSelectionMode.RowHeaderSelect;

            foreach (int row_index in selected_rows)
                dataGridViewHistory.Rows[row_index].Selected = true;
        }

        private void dataGridViewHistory_MouseDown(object sender, MouseEventArgs e)
        {
            // In this callback we have to manually simulate the behavior that selecting a header cell will select
            // an entire column, because the DataGridView class only allows *either* the selection mode of RowHeaderSelect
            // or the selection mode of ColumnHeaderSelect. Both modes at the same time is not supported, but we 
            // can fake it here by manually hit testing for row/column index -1 (i.e. the header row/column)
            // and then changing the selection mode accordingly.
            // This approach was copied from https://social.msdn.microsoft.com/Forums/windows/en-US/570a032e-75d6-4f36-8cf0-9553dc1f46d2/datagridview-with-columnheaderselect-and-rowheaderselect

            System.Windows.Forms.DataGridView.HitTestInfo hti = dataGridViewHistory.HitTest(e.X, e.Y);

            if (hti.ColumnIndex == -1 && hti.RowIndex >= 0)
            {
                // row header click
                if (dataGridViewHistory.SelectionMode != DataGridViewSelectionMode.RowHeaderSelect)
                    dataGridViewHistory.SelectionMode = DataGridViewSelectionMode.RowHeaderSelect;
            }
            else if (hti.RowIndex == -1 && hti.ColumnIndex >= 0) 
            {
                // column header click
                if (dataGridViewHistory.SelectionMode != DataGridViewSelectionMode.ColumnHeaderSelect)
                    dataGridViewHistory.SelectionMode = DataGridViewSelectionMode.ColumnHeaderSelect;
            }
        }

        private void clearFromHistoryToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // Disable updates while we're modifying the data source
            dataGridViewHistory.Enabled = false;
            
            List<HistoryItem> selectedItems = getSelectedHistoryItemsFromHistoryGridView();            
            foreach(HistoryItem item in selectedItems)
                History.RemoveHistoryItem(item);

            dataGridViewHistory.Enabled = true;
        }

        private void deleteFromImgurToolStripMenuItem_Click(object sender, EventArgs e)
        {
            removeItemsFromImgur(getSelectedHistoryItemsFromHistoryGridView());
        }
    }
}
