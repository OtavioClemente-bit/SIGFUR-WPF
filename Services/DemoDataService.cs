using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

/// <summary>
/// Dados sintéticos para apresentação e validação visual. Nunca usa a pasta
/// oficial do usuário: o modo --demo aponta para uma área isolada.
/// </summary>
public static class DemoDataService
{
    public static async Task SeedAsync(MilitaryRepository repository, CancellationToken cancellationToken = default)
    {
        var records = new[]
        {
            New("Cap", "Marina Alves Ribeiro", "MARINA", "00000000001", "D001", "ID-DEMO-001", "Banco Demonstração", "Agência Central"),
            New("1º Ten", "Rafael Costa Nogueira", "RAFAEL", "00000000002", "D002", "ID-DEMO-002", "Banco Demonstração", "Agência Central"),
            New("2º Ten", "Camila Martins Duarte", "CAMILA", "00000000003", "D003", "ID-DEMO-003", "Banco Demonstração", "Agência Central"),
            New("1º Sgt", "Lucas Ferreira Campos", "CAMPOS", "00000000004", "D004", "ID-DEMO-004", "Banco Demonstração", "Agência Central"),
            New("2º Sgt", "Bianca Souza Lima", "BIANCA", "00000000005", "D005", "ID-DEMO-005", "Banco Demonstração", "Agência Central"),
            New("3º Sgt", "Daniel Oliveira Reis", "DANIEL", "00000000006", "D006", "ID-DEMO-006", "Banco Demonstração", "Agência Central")
        };

        foreach (var record in records)
            await repository.SaveAsync(record, cancellationToken);
    }

    private static MilitaryRecord New(string rank, string name, string warName, string cpf, string precCp, string idt, string bank, string agency)
        => new()
        {
            Rank = rank,
            Name = name,
            WarName = warName,
            Cpf = cpf,
            PrecCp = precCp,
            MilitaryId = idt,
            Bank = bank,
            Agency = agency,
            Account = "DEMO-" + precCp,
            FormationYear = "2022",
            CareerType = MilitaryCareerTypeService.Career,
            BirthDate = "10/05/1990",
            EnlistmentDate = "10/01/2022",
            Address = "Endereço fictício de demonstração",
            ZipCode = "00000000",
            ReceivesTransportAid = "Sim",
            TransportAidValue = "320.00",
            TransportGrossTotal = 320,
            TransportWorkingDays = 22,
            Phone = "(00) 00000-0000",
            Email = "demo@sigfur.local",
            IsFavorite = true
        };
}
