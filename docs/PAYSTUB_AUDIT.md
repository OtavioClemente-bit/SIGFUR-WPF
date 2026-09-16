# Auditoria dos contracheques — 06/09/2026

A auditoria identifica os PDFs pelo CPF e pela competência do cabeçalho. Nomes semelhantes e o nome do arquivo não substituem essa identificação. Versões diferentes para a mesma identidade/competência exigem revisão. O cache considera os PDFs, o cadastro e os militares que compõem o escopo.

## Conferência visual

Use **Conferir com prévia** ou dê duplo clique no militar. Uma única janela permite navegar pelos militares, ler o PDF com zoom, ver o nome destacado e consultar os achados. **Marcar verificado** salva a revisão por militar, competência e evidência; o item fica verde. Também há filtros Verificados e Não verificados.

## Auxílio-transporte

O valor mensal normal (NR0095) é separado dos atrasados e das devoluções. A cota-parte mostrada reutiliza a fórmula do sistema: soldo lido no PDF × 6% × dias úteis cadastrados / 30. O líquido calculado só é mostrado quando existe bruto cadastrado. A diferença entre o cadastro e o PDF permanece explícita; não é atribuída automaticamente a arredondamento ou cota-parte.

**Atualizar AT — escolher militares** mostra os candidatos com nome, valor atual, valor do PDF, diferença, cota e prévia. O limite inicial é R$ 5,00, ajustável na janela. Nenhum nome vem marcado. A atualização substitui somente `militares.valor_aux_transporte` pelo valor normal confirmado no PDF; não altera tarifas, bruto, dias, dados bancários ou os demais militares. DR/AR não são somados ou subtraídos do novo valor.

Ficam fora da atualização: valor zero/ausente, leitura ou identidade incerta, pagamento não normal, militar adido/encostado, pagamento de férias no PDF e diferenças acima do limite. O banco e o arquivo são revalidados antes de aplicar. Uma alteração concorrente no cadastro cancela o lote inteiro. Os valores anteriores e novos, o nome, a competência e o PDF ficam registrados na tabela `auditoria_at_atualizacoes`.

## Férias

A fonte exclusiva é o pagamento de férias no PDF da competência auditada. A auditoria não consulta tabelas nem datas do plano de férias. Rubricas de recebimento positivas NR/AR/ER/FR com descrição de férias contam; descontos e valores zero não contam.

Os filtros Com Férias e Sem Férias indicam recebimento no contracheque. Sem PDF válido não significa ausência de férias. Férias pagas com AT normal no mesmo PDF geram FÉRIAS + AT NO PDF — VERIFICAR e impedem a atualização automática do AT. Férias pagas sem AT recebem OK · FÉRIAS PAGAS, SEM AT, conforme o critério solicitado. Os demais achados continuam independentes.

Resultados e marcações da classificação anterior são invalidados pela nova versão da leitura. Refaça a auditoria ao abrir esta versão.

## Validação local

Os testes usam banco isolado; o cadastro real não foi atualizado durante o desenvolvimento. A conferência de agosto usa os arquivos reais e valida CPF e competência no cabeçalho.

Agosto/2026: 177 militares, 176 PDFs encontrados e 1 ausente. 16 contracheques com pagamento de férias; todos os 16 sem AT normal. 50 testes de contracheques/conferência aprovados. A suíte geral teve 157 aprovações e 5 falhas em AdjustmentAccountsEngineTests, fora desta alteração.
