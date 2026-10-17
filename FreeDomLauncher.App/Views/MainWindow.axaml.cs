using Avalonia.Controls;
using FreeDomLauncher.App.Services;

namespace FreeDomLauncher.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        try { Icon = new WindowIcon(AppAssets.Load("icon.png")); } catch { /* иконка не критична */ }
    }
}
