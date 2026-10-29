using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace FrontLineOverlay
{
    public partial class SantaLogoAdorn : UserControl
    {
        // Arquivo da logo grande (mesmo usado pelo MainWindow). Se for outro, altere aqui.
        private const string LogoPath = "assets/logo.png";

        public SantaLogoAdorn()
        {
            InitializeComponent();

            var logo = LoadLogo();
            if (logo != null)
            {
                LogoSizer.Source = logo;
                LogoTintMask.ImageSource = logo;
            }
            else
            {
                LogoTintLayer.Visibility = Visibility.Collapsed;   // sem logo: só o gorro (a cor vem do LogoColor)
            }
        }

        // Tenta como Resource embutido e, depois, como arquivo copiado ao lado do .exe.
        private static BitmapImage? LoadLogo()
        {
            foreach (var root in new[] { "pack://application:,,,/", "pack://siteoforigin:,,,/" })
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(root + LogoPath, UriKind.Absolute);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }
                catch { /* tenta o próximo local */ }
            }
            return null;
        }
    }
}
