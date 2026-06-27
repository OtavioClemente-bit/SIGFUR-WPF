# SIGFUR WPF — Sistema Integrado de Gestão do Furriel

Aplicação desktop desenvolvida em **C# com WPF** para centralizar rotinas administrativas, organizar informações, gerar documentos, apoiar conferências e automatizar tarefas repetitivas.

O projeto nasceu de uma necessidade real: reduzir planilhas soltas, retrabalho manual, conferências demoradas e falta de rastreabilidade em processos administrativos.

> Esta é uma versão pública/portfólio. Dados reais, bancos locais, documentos internos, contracheques, boletins, planilhas sensíveis e arquivos pessoais não devem ser publicados neste repositório.

---

## Visão geral

O **SIGFUR WPF** é a evolução de uma versão inicial em Python/Tkinter para uma base mais robusta em **C# WPF**, com melhor organização visual, separação por módulos e foco em manutenção.

A aplicação foi pensada para uso operacional, com telas específicas para atividades administrativas do dia a dia, persistência local e recursos de automação.

---

## Principais funcionalidades

### Gestão de efetivo

- Cadastro e consulta de militares
- Carteira individual
- Organização por posto/graduação
- Preferências e dados administrativos
- Busca e filtros

### Boletins e documentos

- Boletim inteligente
- Boletim do furriel
- Modelos de texto administrativo
- Geração de documentos
- Consulta e organização por assunto, data e vínculo

### Pagamento e conferência

- Conferência de contracheques
- Auditoria de rubricas
- Auxílio-transporte
- Auxílio-alimentação
- Gratificação
- Exercício anterior
- Pensão judicial
- Ajuste de contas

### Rotina administrativa

- Plano de férias
- Lembretes
- Aniversariantes
- Controle de pendências
- Licenciados e transferidos
- Plano de chamada
- Medidas tomadas

### Automação e produtividade

- Integrações auxiliares
- Automação com Selenium
- Leitura e processamento de documentos
- Persistência de estado da interface
- Backup e restauração

---

## Tecnologias utilizadas

- **C#**
- **WPF**
- **.NET**
- **SQLite**
- **OpenXML**
- **Selenium WebDriver**
- **Manipulação de PDF**
- **Python auxiliar em bridge**
- **Git e GitHub**

---

## Estrutura do projeto

```text
SIGFUR.Wpf/
├── Assets/       # Ícones e identidade visual
├── bridge/       # Automações auxiliares em Python
├── Controls/     # Componentes visuais reutilizáveis
├── Converters/   # Conversores WPF
├── Models/       # Modelos de domínio
├── Resources/    # Recursos públicos seguros
├── Services/     # Regras de negócio, automações e persistência
├── Themes/       # Estilos e tokens visuais
├── Tools/        # Utilitários internos
├── ViewModels/   # Estado e comandos da interface
├── Views/        # Telas e janelas
├── App.xaml
├── App.xaml.cs
├── MainWindow.xaml
├── MainWindow.xaml.cs
└── SIGFUR.Wpf.csproj
```

---

## Como abrir o projeto

1. Instale o **.NET SDK** compatível com o projeto.
2. Abra o arquivo `SIGFUR.Wpf.csproj` no Visual Studio.
3. Restaure os pacotes NuGet.
4. Execute em ambiente Windows.

```bash
dotnet restore
dotnet build
```

> Observação: este projeto usa WPF, portanto é voltado para Windows.

---

## Cuidados da versão pública

A versão original do SIGFUR foi criada para apoiar rotinas reais. Por isso, a versão publicada deve sempre preservar privacidade e segurança.

Foram removidos ou devem permanecer fora do GitHub:

- Bancos reais (`.db`, `.sqlite`, `.sqlite3`)
- Contracheques
- Boletins reais
- PDFs internos
- Planilhas sensíveis
- Documentos pessoais
- Arquivos `.docx`, `.odt`, `.xlsm` com modelos internos
- Pastas `bin/`, `obj/`, `.venv/`, `tmp/`, `PUBLICADO/`
- Arquivos de cache, backup e compilação temporária
- Credenciais, senhas, tokens ou caminhos locais de máquina

---

## Status

🚧 Em evolução contínua  
⭐ Projeto principal do meu portfólio  
🔒 Versão pública preparada com cuidado para não expor dados sensíveis

---

## O que este projeto demonstra

- Capacidade de transformar problemas reais em software
- Construção de sistema desktop com múltiplos módulos
- Organização de regras de negócio
- Uso de persistência local
- Automação de tarefas administrativas
- Evolução técnica de Python/Tkinter para C# WPF
- Preocupação com usabilidade, operação e manutenção

---

## Autor

<<<<<<< HEAD
Desenvolvido por **Otavio Clemente**.

- GitHub: [@OtavioClemente-bit](https://github.com/OtavioClemente-bit)
- LinkedIn: [Otavio Clemente](https://www.linkedin.com/in/otavio-clemente-36056b2b5/)
=======
Desenvolvido por **Otavio Clemente**  
Estudante de Ciência da Computação e desenvolvedor em formação.

- GitHub: [@OtavioClemente-bit](https://github.com/OtavioClemente-bit)
- LinkedIn: [Otavio Clemente](https://www.linkedin.com/in/otavio-clemente-36056b2b5/)
>>>>>>> fe87652a80759e6a4d263311d4dd24acdef100ba
