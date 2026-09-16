using System.Collections.ObjectModel;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.ViewModels.Military;

public sealed class MilitaryEditorViewModel : ObservableObject
{
    private readonly MilitaryRepository _repository;
    private MilitaryRecord _military;
    private bool _isBusy;
    private string _statusText = "Preencha os dados e salve as alterações.";

    public MilitaryEditorViewModel(MilitaryRepository repository, MilitaryRecord military)
    {
        _repository = repository;
        _military = military.Clone();
    }

    public MilitaryRecord Military { get => _military; set => SetProperty(ref _military, value); }
    public ObservableCollection<string> Ranks { get; } = [];
    public ObservableCollection<string> Banks { get; } = [];
    public ObservableCollection<string> YesNo { get; } = ["Não", "Sim"];
    public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    public async Task InitializeAsync()
    {
        IsBusy = true;
        StatusText = "Carregando catálogos do cadastro…";
        try
        {
            Ranks.Clear();
            foreach (var item in (await _repository.GetRanksAsync())
                         .Where(x => MilitaryRankService.GetOrder(x) < 999)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                Ranks.Add(item);
            Banks.Clear();
            foreach (var item in await _repository.GetBanksAsync()) Banks.Add(item);
            if (!string.IsNullOrWhiteSpace(Military.Rank))
            {
                var canonicalRank = MilitaryRankService.Canonicalize(Military.Rank);
                if (MilitaryRankService.GetOrder(canonicalRank) < 999)
                {
                    Military.Rank = canonicalRank;
                    if (!Ranks.Contains(canonicalRank, StringComparer.OrdinalIgnoreCase)) Ranks.Add(canonicalRank);
                }
                else
                {
                    // Nunca reaproveita texto bancário ou outro conteúdo inválido como P/G.
                    Military.Rank = string.Empty;
                }
            }
            if (!string.IsNullOrWhiteSpace(Military.Bank))
            {
                var catalogBank = Banks.FirstOrDefault(x => MilitaryBankService.SameBank(x, Military.Bank));
                if (!string.IsNullOrWhiteSpace(catalogBank)) Military.Bank = catalogBank;
                else Banks.Add(Military.Bank.Trim());
            }
            StatusText = "Preencha os dados e salve as alterações.";
        }
        finally { IsBusy = false; }
    }

    public async Task<int> SaveAsync()
    {
        IsBusy = true;
        StatusText = "Salvando dados no SQLite…";
        try
        {
            var id = await _repository.SaveAsync(Military);
            StatusText = "Dados salvos com sucesso.";
            return id;
        }
        finally { IsBusy = false; }
    }
}
