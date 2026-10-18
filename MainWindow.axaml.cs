using Avalonia.Controls;
using Avance_Player.ViewModels;

namespace Avance_Player;

public partial class MainWindow : Window {
    public MainWindow() {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
        Closed += (_, _) => (DataContext as MainWindowViewModel)?.Dispose();
    }
}
