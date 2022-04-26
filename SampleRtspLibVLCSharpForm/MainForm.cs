using LibVLCSharp.Shared;
using SharpGL;
using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace SampleRtspLibVLCSharpForm
{
    public partial class MainForm : Form
    {
        readonly LibVLC _libVLC;
        readonly MediaPlayer _mediaPlayer;
        private readonly static object _myLock = new object();
        IntPtr _buffer = IntPtr.Zero;
        uint _videoWidth = 1280;
        uint _videoHeight = 720;
        //readonly string _file = @"C:\Users\user\Desktop\dd_test1.avi";
        readonly string _rtspAddress = @"rtsp://admin:123456@172.30.1.32/streaming/channel/101";
        
        readonly string _destinationFolder = Path.Combine(Path.GetDirectoryName(Assembly.GetEntryAssembly().Location), "thumbnails");

        // vlc 옵션
        readonly string[] _vlcOptions = {
            ":network-caching=200",
            ":live-caching=200",
        };

        public MainForm()
        {
            InitializeComponent();

            var currentAssembly = Assembly.GetEntryAssembly();
            var currentDirectory = new FileInfo(currentAssembly.Location).DirectoryName;
            var libDirectory = new DirectoryInfo(System.IO.Path.Combine(currentDirectory, "libvlc", IntPtr.Size == 4 ? "win-x86" : "win-x64"));

            Core.Initialize(libDirectory.FullName);

            this._libVLC = new LibVLC(
                new string[] {
                    "--intf", "dummy",
                    "--vout", "dummy",
                    "--no-snapshot-preview",
                    "--no-osd",
                    "--avcodec-hw=d3d11va",
                    "--no-video-title",
                    "--no-stats",
                    "--skip-frames",
                    "--no-audio",
                    "--no-sub-autodetect-file"
                });
            this._mediaPlayer = new MediaPlayer(this._libVLC);
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            this._mediaPlayer.EnableMouseInput = false;
            this._mediaPlayer.EnableKeyInput = false;

            //this._mediaPlayer.Paused += this.MediaPlayer_Paused;
            //this._mediaPlayer.Playing += this.MediaPlayer_Playing;
            //this._mediaPlayer.Stopped += this.MediaPlayer_Stopped;
            //this._mediaPlayer.EndReached += this.MediaPlayer_EndReached;
            //this._mediaPlayer.PositionChanged += this.MediaPlayer_PositionChanged;

            // case file
            this._mediaPlayer.LengthChanged += this.MediaPlayer_LengthChanged;

            this._mediaPlayer.EndReached += (sender2, e2) =>
            {
                _reconnectCount = 30;
            };

            var lastSnapshot = 0L;
            _mediaPlayer.TimeChanged += (sender2, e2) =>
            {
                var snapshotInterval = e2.Time / 5000;

                if (snapshotInterval > lastSnapshot)
                {
                    lastSnapshot = snapshotInterval;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        _mediaPlayer.TakeSnapshot(0, Path.Combine(_destinationFolder, $"{snapshotInterval}.png"), 1024, 0);
                    });
                }
            };

            this._mediaPlayer.Hwnd = this.pMoiveHost.Handle;
            this._mediaPlayer.EnableHardwareDecoding = false;
            //this._mediaPlayer.Media = new Media(this._libVLC, _file);
            this._mediaPlayer.Media = new Media(this._libVLC, new Uri(_rtspAddress), _vlcOptions);
            this._mediaPlayer.Play();
            
            //ShowOSD("aaa");
            ShowDrawRedOverlay(openGLControl1.Width, openGLControl1.Height);
        }


        private void MediaPlayer_LengthChanged(object sender, MediaPlayerLengthChangedEventArgs e)
        {
            //var timeLength = TimeSpan.FromMilliseconds(e.Length);
            //this._mediaPlayer.Size(0, ref this._videoWidth, ref this._videoHeight);
            this._mediaPlayer.SetVideoCallbacks(
                new MediaPlayer.LibVLCVideoLockCb((IntPtr opaque, IntPtr planes) =>
                {
                    Monitor.Enter(_myLock);
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(this._buffer);
                    this._buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)this._videoWidth * (int)this._videoHeight * 3);
                    System.Runtime.InteropServices.Marshal.WriteIntPtr(planes, this._buffer);

                    return IntPtr.Zero;
                }),
                new MediaPlayer.LibVLCVideoUnlockCb((IntPtr opaque, IntPtr picture, IntPtr planes) =>
                {
                    Monitor.Exit(_myLock);
                }), new MediaPlayer.LibVLCVideoDisplayCb((IntPtr opaque, IntPtr picture) =>
                {
                    try
                    {
                        this.Invoke(new Action(this.openGLControl1.DoRender));
                    }
                    catch { }
                }));
            this._mediaPlayer.SetVideoFormat("RV24", this._videoWidth, this._videoHeight, this._videoWidth * 3);
        }

        readonly object _osdLock = new object();
        Bitmap _osdMessage;
        BackgroundWorker _osdDelayTask;
        readonly int _osdDelay = 2000;

        private void ShowOSD(string message)
        {
            if (this._osdDelayTask != null)
            {
                this._osdDelayTask.CancelAsync();
            }

            lock (this._osdLock)
            {
                if (this._osdMessage != null)
                {
                    this._osdMessage.Dispose();
                    this._osdMessage = null;
                }

                var ff = new FontFamily("Microsoft YaHei UI");
                var f = new Font(ff, 24, FontStyle.Regular, GraphicsUnit.Point);
                var gp = new System.Drawing.Drawing2D.GraphicsPath();
                gp.AddString(message, ff, (int)FontStyle.Regular, 18, Point.Empty, StringFormat.GenericDefault);
                var gpb = gp.GetBounds();

                Bitmap text = new Bitmap((int)gpb.Width + 10, (int)gpb.Height + 10);
                using (var tg = Graphics.FromImage(text))
                {
                    tg.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    tg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    tg.FillPath(Brushes.Yellow, gp);
                    tg.Dispose();
                }

                this._osdMessage = text;
            }
            this.Invoke(new Action(this.openGLControl1.DoRender));

            this._osdDelayTask = new BackgroundWorker();
            this._osdDelayTask.WorkerSupportsCancellation = true;
            this._osdDelayTask.DoWork += (sender, e) =>
            {
                Thread.Sleep(this._osdDelay);
                if (((BackgroundWorker)sender).CancellationPending)
                {
                    e.Cancel = true;
                    return;
                }
            };
            this._osdDelayTask.RunWorkerCompleted += (sender, e) =>
            {
                if (e.Cancelled)
                {
                    return;
                }
                lock (this._osdLock)
                {
                    if (this._osdMessage != null)
                    {
                        this._osdMessage.Dispose();
                        this._osdMessage = null;
                    }
                }
                this.Invoke(new Action(this.openGLControl1.DoRender));
            };
            this._osdDelayTask.RunWorkerAsync();
        }

        readonly object _overlayLock = new object();
        Bitmap _overlayBitmap;

        private void ShowDrawRedOverlay(int width, int height)
        {
            lock (this._overlayLock)
            {
                if (this._overlayBitmap != null)
                {
                    this._overlayBitmap.Dispose();
                    this._overlayBitmap = null;
                }

                var gp = new System.Drawing.Drawing2D.GraphicsPath();
                gp.AddRectangle(new Rectangle(0, 0, width, height));
                Bitmap overlay = new Bitmap(width, height);
                using (var tg = Graphics.FromImage(overlay))
                {
                    tg.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    tg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    Brush red = new SolidBrush(Color.FromArgb(80, 255, 0, 0));
                    tg.FillPath(red, gp);
                    tg.Dispose();
                }

                this._overlayBitmap = overlay;
            }
            this.Invoke(new Action(this.openGLControl1.DoRender));
        }

        private void OpenGLControl1_OpenGLDraw(object sender, RenderEventArgs args)
        {
            SharpGL.OpenGL gl = this.openGLControl1.OpenGL;
            gl.Clear(OpenGL.GL_COLOR_BUFFER_BIT | OpenGL.GL_DEPTH_BUFFER_BIT);
            gl.MatrixMode(OpenGL.GL_PROJECTION);
            gl.LoadIdentity();
            Monitor.Enter(MainForm._myLock);
            try
            {
                if (this._buffer != IntPtr.Zero)
                {
                    var xz = (float)this.openGLControl1.Width / (float)this._videoWidth;
                    var yz = (float)this.openGLControl1.Height / (float)this._videoHeight;

                    // case1
                    //var xyz = 1f;
                    //xyz = Math.Min(xz, yz);
                    //if (xz < yz)
                    //{
                    //    gl.RasterPos(-1f, (xyz / yz));
                    //}
                    //else
                    //{
                    //    gl.RasterPos((xyz / xz), -1f);
                    //}
                    //gl.PixelZoom(xyz, -xyz);

                    // case2
                    gl.RasterPos(-1f, 1f);
                    gl.PixelZoom(xz, -yz);

                    gl.DrawPixels((int)this._videoWidth, (int)this._videoHeight, OpenGL.GL_RGB, OpenGL.GL_UNSIGNED_BYTE, this._buffer);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
            finally
            {
                Monitor.Exit(_myLock);
            }

            lock (this._overlayLock)
            {
                if (this._overlayBitmap != null)
                {
                    System.Drawing.Imaging.BitmapData overlayData = this._overlayBitmap.LockBits(new Rectangle(0, 0, this._overlayBitmap.Width, this._overlayBitmap.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    //var x = (2f / this.openGLControl1.Width) * 5f - 1f;
                    //var y = (2f / this.openGLControl1.Height) * (this.openGLControl1.Height - 5f) - 1f;
                    //gl.RasterPos(x, y);
                    gl.RasterPos(-1f, 1f);
                    gl.PixelZoom(1, -1);
                    gl.DrawPixels(this._overlayBitmap.Width, this._overlayBitmap.Height, OpenGL.GL_BGRA, OpenGL.GL_UNSIGNED_BYTE, overlayData.Scan0);
                    this._overlayBitmap.UnlockBits(overlayData);
                }
            }
            //lock (this.osdLock)
            //{
            //    if (this.osdMessage != null)
            //    {
            //        var textData = this.osdMessage.LockBits(new Rectangle(0, 0, this.osdMessage.Width, this.osdMessage.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            //        var x = (2f / this.openGLControl1.Width) * 50f - 1f;
            //        var y = (2f / this.openGLControl1.Height) * (this.openGLControl1.Height - 50f) - 1f;
            //        gl.RasterPos(x, y);
            //        gl.PixelZoom(1, -1);
            //        gl.DrawPixels(this.osdMessage.Width, this.osdMessage.Height, OpenGL.GL_BGRA, OpenGL.GL_UNSIGNED_BYTE, textData.Scan0);
            //        this.osdMessage.UnlockBits(textData);
            //        //gl.DrawText(50, this.openGLControl1.Height - 18, 1, 1, 0, "맑은 고딕", 24, "aaaa");
            //    }
            //}

            gl.Flush();
        }

        private void OpenGLControl1_OpenGLInitialized(object sender, EventArgs e)
        {
            OpenGL gl = this.openGLControl1.OpenGL;
            gl.Enable(OpenGL.GL_BLEND);
            gl.BlendFunc(OpenGL.GL_SRC_ALPHA, OpenGL.GL_ONE_MINUS_SRC_ALPHA);
            gl.ClearColor(0, 0, 0, 0);
        }

        private const int RECONNECT_COUNT = 100;
        private int _reconnectCount = RECONNECT_COUNT;

        private void Timer1_Tick(object sender, EventArgs e)
        {
            if (_mediaPlayer == null)
            {
                return;
            }

            try
            {
                //Trace.WriteLine($"state: {vlcControl1.GetCurrentMedia().State}");
                if (_mediaPlayer.Media.State != VLCState.Playing)
                {
                    //설정된 횟수에 도달할 떄 까지 감소
                    _reconnectCount--;
                    if (_reconnectCount <= 0)
                    {
                        //감소 값 초기화
                        _reconnectCount = RECONNECT_COUNT;
                        //연결이 끊겼다고 판단할 때
                        try
                        {
                            _mediaPlayer.Play(new Media(this._libVLC, new Uri(_rtspAddress), _vlcOptions));
                            //_mediaPlayer.Play(new Media(_libVLC, _file));

                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private void OpenGLControl1_Resize(object sender, EventArgs e)
        {
            if (!openGLControl1.Created)
            {
                return;
            }

            ShowDrawRedOverlay(openGLControl1.Width, openGLControl1.Height);
        }
    }
}
