using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using SIGFUR.Wpf.Views.Finance;

namespace SIGFUR.Wpf.Views.Military;

public sealed class TransportAuditUpdateWindow : Window
{
    private readonly ConferencePdfPreview _preview = new("Contracheque do militar selecionado");
    public sealed class Choice(PaystubAuditRow row)
    {
        public bool Selected { get; set; }
        public PaystubAuditRow Row { get; } = row;
    }
    public TransportAuditUpdateWindow(IReadOnlyList<PaystubAuditRow> rows, PaystubAuditWorkflow workflow)
    {
        Title = "Atualizar auxílio-transporte — escolha os militares"; Width = 1450; Height = 820; MinWidth = 1050;MinHeight=650;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(12),Background=System.Windows.Media.Brushes.White};Content=root;
        var bar=new WrapPanel();bar.Children.Add(new TextBlock{Text="Diferença máxima (R$): ",VerticalAlignment=VerticalAlignment.Center});
        var limit=new ComboBox{ItemsSource=new double[]{1,5,10,20,50},SelectedItem=5d,Width=80,Margin=new Thickness(4)};bar.Children.Add(limit);
        var label=new TextBlock{Margin=new Thickness(8),TextWrapping=TextWrapping.Wrap};bar.Children.Add(label);DockPanel.SetDock(bar,Dock.Top);root.Children.Add(bar);
        var note=new TextBlock{Text="Escolha quem terá o líquido mensal cadastrado substituído pela rubrica normal NR0095. Atrasados e devoluções não são somados a esse valor. Pagamento de férias no PDF e leituras duvidosas ficam fora da atualização.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(4,8,4,10)};DockPanel.SetDock(note,Dock.Top);root.Children.Add(note);
        var apply=new Button{Content="Aplicar aos militares marcados",Padding=new Thickness(16,8,16,8),HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(4,10,4,4)};DockPanel.SetDock(apply,Dock.Bottom);root.Children.Add(apply);
        var split=new Grid();split.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});split.ColumnDefinitions.Add(new(){Width=new GridLength(8)});split.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        var grid=new DataGrid{AutoGenerateColumns=false,CanUserAddRows=false,SelectionMode=DataGridSelectionMode.Single};
        grid.Columns.Add(new DataGridCheckBoxColumn{Header="✓",Binding=new Binding(nameof(Choice.Selected)){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged},Width=40});
        foreach(var (title,path,width) in new[]{("Militar","Row.Name",205d),("Atual","Row.AuxDatabaseText",90d),("PDF / novo","Row.AuxPdfText",95d),("Diferença","Row.AuxDifferenceText",90d),("Cota-parte","Row.TransportShareText",90d)})
        {
            var cellStyle=new Style(typeof(TextBlock));cellStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty,TextWrapping.Wrap));cellStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,new Binding(path)));
            grid.Columns.Add(new DataGridTextColumn{Header=title,Binding=new Binding(path),Width=width,IsReadOnly=true,ElementStyle=cellStyle});
        }
        var preview=_preview;split.Children.Add(grid);Grid.SetColumn(preview,2);split.Children.Add(preview);root.Children.Add(split);
        var choices=new List<Choice>();
        void Refresh(){choices=rows.Where(r=>r.CanUpdateTransport((double)limit.SelectedItem)).OrderBy(r=>r.Name).Select(r=>new Choice(r)).ToList();grid.ItemsSource=choices;label.Text=$"{choices.Count} candidato(s). Nenhum selecionado automaticamente.";apply.IsEnabled=choices.Count>0;}
        limit.SelectionChanged+=(_,_)=>Refresh();Refresh();
        grid.SelectionChanged+=async(_,_)=>{if(grid.SelectedItem is Choice item)await preview.ShowAsync(item.Row.PdfPath,1,item.Row.Name);};
        apply.Click+=async(_,_)=>
        {
            grid.CommitEdit(DataGridEditingUnit.Cell,true);grid.CommitEdit(DataGridEditingUnit.Row,true);
            var selected=choices.Where(c=>c.Selected).Select(c=>c.Row).ToList();
            if(selected.Count==0){label.Text="Marque pelo menos um militar na coluna ✓.";return;}
            root.IsEnabled=false;
            try{var count=await workflow.UpdateTransportAsync(selected,(double)limit.SelectedItem);MessageBox.Show(this,$"{count} cadastro(s) atualizado(s). Valores anteriores registrados no histórico.","Atualização concluída");DialogResult=true;}
            catch(Exception ex){MessageBox.Show(this,ex.Message,"Atualização não aplicada");}
            finally{root.IsEnabled=true;}
        };
    }
}
