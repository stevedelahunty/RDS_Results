using System.Windows;
using System.Windows.Controls;

namespace RDS_Results;

public partial class SettingsPanel : UserControl
{
    internal MainWindow? Host { get; set; }

    public SettingsPanel()
    {
        InitializeComponent();
    }

    void RefreshButton_Click(object sender, RoutedEventArgs e) => Host!.RefreshButton_Click(sender, e);

    void SaveButton_Click(object sender, RoutedEventArgs e) => Host!.SaveButton_Click(sender, e);

    void IntervalBox_LostFocus(object sender, RoutedEventArgs e) => Host!.IntervalBox_LostFocus(sender, e);

    void AutoRefreshBox_Changed(object sender, RoutedEventArgs e) => Host!.AutoRefreshBox_Changed(sender, e);

    void LightThemeButton_Click(object sender, RoutedEventArgs e) => Host!.LightThemeButton_Click(sender, e);

    void DarkThemeButton_Click(object sender, RoutedEventArgs e) => Host!.DarkThemeButton_Click(sender, e);

    void BrowseButton_Click(object sender, RoutedEventArgs e) => Host!.BrowseButton_Click(sender, e);

    void OpenFolderButton_Click(object sender, RoutedEventArgs e) => Host!.OpenFolderButton_Click(sender, e);

    void HostBox_LostFocus(object sender, RoutedEventArgs e) => Host!.HostBox_LostFocus(sender, e);

    void ListenSettings_LostFocus(object sender, RoutedEventArgs e) => Host!.ListenSettings_LostFocus(sender, e);

    void ListenBox_Changed(object sender, RoutedEventArgs e) => Host!.ListenBox_Changed(sender, e);

    void SendReadsBox_Changed(object sender, RoutedEventArgs e) => Host!.SendReadsBox_Changed(sender, e);

    void SendPortBox_LostFocus(object sender, RoutedEventArgs e) => Host!.SendPortBox_LostFocus(sender, e);

    void BrowseEventGraphicButton_Click(object sender, RoutedEventArgs e) => Host!.BrowseEventGraphicButton_Click(sender, e);

    void ClearEventGraphicButton_Click(object sender, RoutedEventArgs e) => Host!.ClearEventGraphicButton_Click(sender, e);

    void EventWelcome_LostFocus(object sender, RoutedEventArgs e) => Host!.EventWelcome_LostFocus(sender, e);

    void RaceStart_LostFocus(object sender, RoutedEventArgs e) => Host!.RaceStart_LostFocus(sender, e);

    void Weather_LostFocus(object sender, RoutedEventArgs e) => Host!.Weather_LostFocus(sender, e);

    void Camera_LostFocus(object sender, RoutedEventArgs e) => Host!.Camera_LostFocus(sender, e);

    void CameraAdvanced_Changed(object sender, RoutedEventArgs e) => Host!.CameraAdvanced_Changed(sender, e);

    void CameraExact_Changed(object sender, RoutedEventArgs e) => Host!.CameraExact_Changed(sender, e);
}
