using System.Windows;
using System.Windows.Controls;
using Prism.App.ViewModels;
using Prism.Core.Ipc;
namespace Prism.App.Views;
public partial class TokenEntry : UserControl
{
    public TokenEntry()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => { Host.Visibility = HostLabel.Visibility = DataContext is ModemViewModel ? Visibility.Collapsed : Visibility.Visible; };
        Unloaded += (_, _) => Secret.Clear();
    }
    private async void Save(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PageViewModel vm) return;
        SaveButton.IsEnabled = false;
        string value = Secret.Password; Secret.Clear();
        try
        {
            var response = await vm.Client.SendAsync(vm is LampViewModel ? Commands.SetLampToken : Commands.SetRouterPassword,
                vm is LampViewModel ? (object)new { host = Host.Text, token = value } : new { password = value });
            Result.Text = response.Ok ? "Credential saved" : "Credential could not be saved. Check the values and service connection.";
        }
        catch (Exception) { Result.Text = "Credential could not be saved. Check the service connection."; }
        finally { value = string.Empty; SaveButton.IsEnabled = true; }
    }
}
