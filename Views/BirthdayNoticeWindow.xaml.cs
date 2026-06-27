using System.Windows;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views;

public partial class BirthdayNoticeWindow : Window
{
    public BirthdayNoticeWindow(IReadOnlyList<BirthdayItem> birthdays)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        BirthdaysList.ItemsSource = birthdays.Select(x => new BirthdayNoticeItem
        {
            Rank = MilitaryRankService.ShortName(x.Rank),
            NameBeforeWar = x.NameBeforeWar,
            NameWarBold = x.NameWarBold,
            NameAfterWar = x.NameAfterWar,
            Age = x.Age
        }).ToList();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private sealed class BirthdayNoticeItem
    {
        public string Rank { get; set; } = string.Empty;
        public string NameBeforeWar { get; set; } = string.Empty;
        public string NameWarBold { get; set; } = string.Empty;
        public string NameAfterWar { get; set; } = string.Empty;
        public int Age { get; set; }
    }
}
