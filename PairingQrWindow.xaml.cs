using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QRCoder;

namespace WindowsNotch
{
    public partial class PairingQrWindow : Window
    {
        private readonly DispatcherTimer _timer;
        private DateTime _expiresAt;

        public PairingQrWindow(int port, string pcId)
        {
            InitializeComponent();

            string token = PairingService.GeneratePairingToken();
            string host = PairingService.GetLocalIpAddress();
            string pcName = Environment.MachineName;

            string payload = PairingService.BuildQrPayload(host, port, token, pcId, pcName);
            GenerateQrBitmap(payload);

            _expiresAt = DateTime.UtcNow.AddMinutes(3);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) =>
            {
                var remaining = _expiresAt - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    _timer.Stop();
                    ExpiryText.Text = "Kode QR sudah kedaluwarsa";
                    PairingService.InvalidatePairingToken();
                }
                else
                {
                    ExpiryText.Text = $"Kode QR berlaku ({remaining.Minutes:D2}:{remaining.Seconds:D2})";
                }
            };
            _timer.Start();

            PairingService.PairingApproved += OnPairingApproved;
            CloseButton.Click += (s, e) => Close();
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) Close();
            };
        }

        private void OnPairingApproved()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(Close));
                return;
            }

            Close();
        }

        private void GenerateQrBitmap(string payload)
        {
            using var generator = new QRCodeGenerator();
            using var qrData = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
            using var qrCode = new PngByteQRCode(qrData);
            byte[] qrBytes = qrCode.GetGraphic(20);

            var image = new BitmapImage();
            using (var stream = new MemoryStream(qrBytes))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
            }

            QrImage.Source = image;
        }

        protected override void OnClosed(EventArgs e)
        {
            PairingService.PairingApproved -= OnPairingApproved;
            _timer.Stop();
            base.OnClosed(e);
        }
    }
}
