using System.Windows;
using System.Windows.Controls;
using Prism.App.ViewModels;
namespace Prism.App.Views;
public partial class TokenEntry : UserControl
{
    public TokenEntry() { InitializeComponent(); }
    private void TokenChanged(object sender, RoutedEventArgs e)
    { if (DataContext is LampViewModel vm) vm.MiHomeToken = ((PasswordBox)sender).Password; }
}
