using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Views.Finance;

namespace SIGFUR.Wpf.Views.Military;

public sealed class PaystubAuditReviewWindow : Window
{
    private readonly ListBox _people = new();
    private readonly ConferencePdfPreview _preview = new("Contracheque · nome destacado");
    private readonly TextBox _detail = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12) };
    public PaystubAuditReviewWindow(Func<PaystubAuditRow, bool, Task> verify, Func<PaystubAuditRow, string> details)
    {
        Title = "Auditoria visual de contracheques"; Width = 1500; Height = 880; MinWidth = 1050; MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(233,239,247)) };
        var bar = new WrapPanel { Margin = new Thickness(8) };
        foreach (var (label, step) in new[] { ("◀ Anterior", -1), ("Próximo ▶", 1) })
        {
            var button = new Button { Content = label, Margin = new Thickness(4), Padding = new Thickness(12,6,12,6) };
            button.Click += (_,_) => { if(_people.Items.Count>0) { _people.SelectedIndex=Math.Clamp(_people.SelectedIndex+step,0,_people.Items.Count-1);_people.ScrollIntoView(_people.SelectedItem); } };bar.Children.Add(button);
        }
        var verified = new Button { Margin = new Thickness(4), Padding = new Thickness(12,6,12,6) };
        verified.SetBinding(ContentControl.ContentProperty,new Binding("SelectedItem.VerificationText") { Source = _people });
        verified.Click += async (_,_) => { if(_people.SelectedItem is not PaystubAuditRow row)return;verified.IsEnabled=false;
            try { await verify(row,!row.IsVerified); } catch(Exception ex){MessageBox.Show(this,ex.Message,"Falha ao salvar");}finally{verified.IsEnabled=true;} };
        bar.Children.Add(verified); DockPanel.SetDock(bar,Dock.Top);root.Children.Add(bar);
        var grid=new Grid(); foreach(var width in new[]{new GridLength(260),new GridLength(5),new GridLength(1,GridUnitType.Star),new GridLength(5),new GridLength(360)})grid.ColumnDefinitions.Add(new(){Width=width});
        var template=new DataTemplate();var labelText=new FrameworkElementFactory(typeof(TextBlock));labelText.SetBinding(TextBlock.TextProperty,new Binding(nameof(PaystubAuditRow.AuditReviewLabel)));labelText.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);template.VisualTree=labelText;_people.ItemTemplate=template;
        ScrollViewer.SetHorizontalScrollBarVisibility(_people,ScrollBarVisibility.Disabled);
        var style=new Style(typeof(ListBoxItem));style.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(8)));style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Stretch));
        var trigger=new DataTrigger{Binding=new Binding(nameof(PaystubAuditRow.IsVerified)),Value=true};trigger.Setters.Add(new Setter(Control.BackgroundProperty,new SolidColorBrush(Color.FromRgb(209,245,218))));style.Triggers.Add(trigger);_people.ItemContainerStyle=style;
        grid.Children.Add(_people);Grid.SetColumn(_preview,2);grid.Children.Add(_preview);Grid.SetColumn(_detail,4);grid.Children.Add(_detail);
        foreach(var col in new[]{1,3}){var splitter=new GridSplitter{Width=5,HorizontalAlignment=HorizontalAlignment.Stretch};Grid.SetColumn(splitter,col);grid.Children.Add(splitter);}root.Children.Add(grid);Content=root;
        _people.SelectionChanged += async (_,_) => { if(_people.SelectedItem is PaystubAuditRow row){_detail.Text=details(row);await _preview.ShowAsync(row.PdfPath,1,row.Name);} };
        Loaded += (_,_)=>{if(_people.SelectedItem is not null)_people.ScrollIntoView(_people.SelectedItem);};
    }
    public void ShowRows(IReadOnlyList<PaystubAuditRow> rows, PaystubAuditRow? selected) { _people.ItemsSource=rows;_people.SelectedItem=selected??rows.FirstOrDefault();if(_people.SelectedItem is not null)_people.ScrollIntoView(_people.SelectedItem); }
}
