# Conferência de pagamento — revisão de 06/09/2026

A conferência compara publicações com a evidência disponível nos contracheques da pasta selecionada. Ela não consulta o estado de homologação ou de processamento de lançamentos no SIPPES.

## Presença por código ou nome e conferência visual

**Verificação manual:** a coluna Verificação e o botão no lado a lado permitem marcar/desmarcar cada item. Os verificados ficam verdes nas duas listas, sem alterar a classificação automática. A marcação é salva imediatamente no perfil, em `conferencia_pagamento/verificados.json`, e reaplicada ao reabrir ou recalcular. É específica da competência, publicação, militar e evidência; PDFs ou resultados alterados exigem nova verificação. Há filtros de verificados/não verificados e a exportação CSV inclui a marcação.

**Nome em amarelo:** ao selecionar o militar, o nome publicado é localizado pelas coordenadas do texto na página e destacado na prévia, inclusive com quebra de linha. O destaque acompanha o zoom, é substituído na troca de militar e não modifica o PDF original. Se não houver texto correspondente na página, a prévia informa que o nome não foi localizado.

Validação desta melhoria: 32 testes específicos aprovados, incluindo persistência/desmarcação, isolamento entre competências/publicações e localização de nome com acentos/quebra de linha. Destaque amarelo validado visualmente no ADT 27 real.

A opção **Aceitar presença por código ou nome** vem marcada. Nesse modo, código exato ou descrição reconhecida do benefício confirma **ACHOU RUBRICA** na identidade e competência corretas. O detalhe registra o critério, o código efetivamente encontrado e eventuais valores diferentes. Confirmação de presença não certifica valor, referência ou atribuição individual a cada publicação. Desmarcar a opção mantém a conferência estrita anterior, com código, natureza, valor e alertas sobre evidência compartilhada.

A busca por descrição reconhece, por exemplo, auxílio-alimentação e suas abreviações; não confunde pensão alimentícia com o auxílio. Receita normal não comprova devolução/desconto. Rubricas presentes em várias publicações permanecem localizadas no modo de presença, com observação sobre a necessidade de conferir as referências.

Os resultados seguem a data do boletim, seu número e a página, preservando a ordem dos militares na publicação. O botão **Conferir lado a lado** abre uma única janela reutilizável com lista de militares, publicação na página correspondente e contracheque da competência. Clicar em um nome troca as duas prévias. Há navegação por militar e página, zoom até 200% e divisórias ajustáveis. Os PDFs são renderizados localmente; não é aberto um aplicativo externo por militar.

### Cruzamento de agosto/2026 — ADTs 26, 27 e 28

Na pasta do perfil Otávio foram encontrados 176 contracheques de agosto e excluídos 1.155 PDFs de outras competências. Os três boletins geraram 210 itens: 8 do ADT 26, 147 do ADT 27 e 55 do ADT 28.

Com o critério de presença solicitado: 167 itens com rubrica localizada, 29 sem rubrica, 4 com natureza diferente, 2 sem contracheque, 1 para revisão e 7 atos cadastrais. Antes da alteração havia 75 localizados, 58 sem rubrica e 61 para revisão. As duas publicações que ordenam suspensão de pagamento passaram a atos cadastrais, com contracheque disponível para leitura quando identificado.

Exemplo verificado nos PDFs: publicação de alimentação com AR0058 e contracheque com NR0054 — AUXÍLIO ALIMENTAÇÃO C. Agora a presença é reconhecida pela descrição e o código real permanece visível. Os quatro casos de natureza diferente pedem devolução de transporte, mas apresentam apenas recebimento normal na folha.

Validação atual: **30 testes específicos aprovados**, incluindo critérios estrito e de presença, identidade, competência, nomes semelhantes de benefícios, recebimento versus devolução, rubricas compartilhadas e suspensão cadastral. A prévia foi renderizada com boletim e contracheque reais e foi verificado que uma troca rápida para item sem PDF não deixa a imagem anterior na tela. O CSV `CONFERENCIA_AGOSTO_ADT_26_27_28.csv`, junto ao executável, contém os 210 resultados desse cruzamento.

## Referências e regras

- Fonte: Manual Técnico do SIPPES de 17/07/2026. O recurso incorporado `Resources/PaymentConference/sippes-rubrics-2026-07-17.json` indexa 197 códigos mencionados no texto e suas páginas; não pretende ser uma relação exaustiva de códigos vigentes.
- Um código escrito no bloco individual do boletim tem prioridade. Sem código explícito, são usadas somente as regras específicas implementadas em `PaymentConferenceService.Interpretation.cs` e respaldadas pelo manual. Uma descrição genérica não confirma qualquer rubrica.
- Auxílio-transporte: NR0095 normal, AR0095 atrasado, FR0095 diferença, DR0095 devolução (manual, páginas 128–132).
- Alimentação: respeita o código publicado e não é classificada como adicional de férias só porque o direito decorre de férias (manual, páginas 132–133).
- Adicional de férias em ajuste de contas e indenização são itens separados, AR0096 e AR0094 (manual, páginas 179–180 e 189–190).
- Códigos explícitos sem referência encontrada no manual permanecem visíveis, exigindo revisão. Exemplo no boletim fornecido: AR0048. Não há substituição automática por AR0058.
- O mês de referência do direito não é tratado automaticamente como mês da folha. Somente uma determinação explícita do mês de pagamento restringe a competência da publicação.

## Evidência e resultados

O leitor preserva a página física e mantém identidades com CPF mesmo quando o formato do nome não é reconhecido. O cadastro ajuda a identificar o militar, mas sua ausência não impede conferir um PDF com a identidade publicada.

A busca lê somente a pasta selecionada e valida CPF/PREC e competência no cabeçalho. Semelhança de nome, nome do arquivo e contracheques de outras competências não produzem confirmação automática. Fichas financeiras e espelhos coletivos nomeados como tais não são tratados como contracheques individuais.

Mês e ano são selecionados em ComboBoxes. Ao trocar a competência, a tela limpa o resultado anterior e preserva os boletins marcados. A nova conferência usa exatamente esses boletins, independentemente do mês em que foram publicados.

Um índice lê o cabeçalho da primeira página dos PDFs novos ou alterados para identificar a competência, incluindo os formatos SIPPES e CPEx. Somente os contracheques do mês **e** ano escolhidos passam para a leitura integral e a comparação de rubricas. O nome do arquivo e a data de emissão não determinam a competência. PDFs sem competência identificável ficam fora da comparação e geram aviso separado.

A aba **Contracheques da competência** mostra os arquivos efetivamente usados, com competência, CPF e caminho. O resumo informa quantos contracheques e boletins entraram e quantos PDFs de outras competências foram excluídos. O detalhe e o CSV registram a competência conferida.

- **ACHOU RUBRICA:** código ou descrição localizada, conforme o critério escolhido; o detalhe distingue presença de validação estrita do valor.
- **NÃO ACHOU RUBRICA:** código ausente no PDF identificado da competência selecionada. Não significa que inexista um lançamento pendente no SIPPES.
- **VALOR DIVERGENTE / NATUREZA DIFERENTE:** evidência diferente da publicação.
- **REVISÃO NECESSÁRIA:** leitura insuficiente, competência/identidade ambígua ou múltiplas versões. No modo estrito, também inclui código desconhecido, vários lançamentos compatíveis ou publicações que disputam a mesma evidência.
- **ATO CADASTRAL / OUTRA COMPETÊNCIA:** não são contabilizados como ausência de rubrica.

O valor é comparado somente quando é individual e inequívoco. Na tabela SIPPES `VALOR % R/D IR PARC`, o percentual não é somado ao valor. Rubricas com zero ou sem valor legível são preservadas. Não se somam lançamentos repetidos sem evidência dos parâmetros de referência.

O detalhe e o CSV incluem códigos esperados, valores, motivo do resultado, referências, fonte da regra, rubricas encontradas e outras rubricas lidas. Os filtros da tela não reduzem a exportação.

## Cache

A versão do interpretador, configurações, PDFs e banco cadastral/WAL participam da assinatura. Resultados sem assinatura de origem, com arquivos modificados ou com interpretador antigo não são reutilizados. Alterações durante a execução exigem nova conferência.

## Validação

- Boletim fornecido de 24/07/2026: 18 páginas, 100 CPFs distintos preservados, 147 itens após separar os dois direitos de férias. A amostra estrutural permanente usa nomes, CPFs e PRECs fictícios.
- Contracheque SIPPES real: cabeçalho com rótulos/valores em linhas diferentes, competência, identidade e oito rubricas reconhecidos pelo extrator nativo.
- 24 testes específicos aprovados cobrem interpretação, identidade, natureza, valores, ambiguidade, pasta selecionada, cache, a amostra completa e o fluxo com PDFs de meses/anos diferentes. Incluem nome de arquivo enganoso, boletim não marcado, PDF ilegível e formato CPEx antigo.
- Cruzamento com arquivos locais, usando somente o aditamento nº 21 de 03/06/2026: junho/2026 incluiu 9 contracheques; maio/2026 incluiu 1; agosto/2026 e junho/2025 não incluíram nenhum. As 156 linhas de cada rodada vieram exclusivamente do boletim escolhido. Contagens correspondem aos arquivos disponíveis durante o teste.
- Verificação individual nesse cruzamento: a publicação de auxílio-fardamento em exercícios anteriores espera ER0056; o contracheque de junho do militar identificado foi localizado, mas não contém esse código. Ao escolher maio, o mesmo militar ficou sem contracheque, sem reaproveitar o PDF de junho.
- A interação dos seletores foi verificada: anos preenchidos, troca de ano e mês limpa resultados e preserva a marcação dos boletins.
- Tela renderizada para revisão em largura de 1540 e 1180 pixels.
- A rodada geral encontrou cinco falhas em `AdjustmentAccountsEngineTests`, cujos serviços não foram modificados nesta revisão. Os resultados dessa rodada não equivalem a aprovação integral do projeto.

Comando usado para os testes específicos (a partir do projeto WPF):

```powershell
dotnet test ../SIGFUR.Wpf.Tests/SIGFUR.Wpf.Tests.csproj --filter FullyQualifiedName~PaymentConference -p:SelfContained=false -o tmp/payment-conference-review/test-framework
```
