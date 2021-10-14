using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using SteamKit2;
using System;
using System.IO;
using Gameloop.Vdf;
using System.Security.Cryptography;
using System.Threading;
using SteamKit2.GC.CSGO;
using System.Text.Json;
using SteamKit2.GC.CSGO.Internal;
using SteamKit2.Internal;
using SteamKit2.GC;
using SteamKit2.Discovery;
using System.Collections.Generic;
using Nelibur.ObjectMapper;
using Gameloop.Vdf.Linq;

namespace SteamInventoryManager.Views
{

    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
#if DEBUG
            this.AttachDevTools();
#endif

        }


        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
            
        }

    }
}
