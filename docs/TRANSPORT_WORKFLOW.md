# Auxílio-transporte — revisão de 06/09/2026

Fonte: Manual Técnico do SIPPES, versão de 17/07/2026, páginas 128–132. Na página 130 o exemplo da folha de abril determina maio como referência de AR0095. O programa aplica a mesma regra à folha escolhida pelo usuário, incluindo dezembro → janeiro do ano seguinte.

## Atrasados

O seletor informa explicitamente a folha sem recebimento. A dica e a fila mostram a referência que será utilizada no SIPPES e no boletim. A conversão acontece na geração; não modifica o mês originalmente escolhido. A mesma folha informada em dois formatos não duplica o total. Atualização de valores, diferença e DA conservam suas referências próprias.

A AR0095 usa diária bruta, quantidade de dias e referência do benefício. A estimativa de total usa a SAT carregada; devem ser conferidos tarifa, dias, soldo e graduação vigentes no direito. A exceção do Efetivo Variável em dezembro, janeiro e fevereiro impede a geração automática deste modelo para esses casos, conforme item 2 da página 129. Exercícios anteriores têm procedimento próprio.

## Despesa a anular

A importação considera publicações do mês anterior e seguinte, mas filtra a data efetiva de serviço pela competência selecionada. Rótulos das tabelas não cortam mais nomes nem graduações quebradas. Abreviações de nomes são comparadas com a identidade cadastrada. Receber AT não resolve sozinho uma ambiguidade de identidade.

Substituições explícitas retiram o substituído e incluem o substituto na data publicada, preservando trocas em grupo e excluindo duplicidades por militar/dia. Missões com data própria são separadas do cabeçalho da escala. A prévia mostra cobertura, dias sem escala, substituições e identificações pendentes. Pendências não são atribuídas automaticamente; devem ser conferidas e ajustadas na tabela antes da geração definitiva.

A regra operacional existente de pretas menos vermelhas foi mantida, com limite do valor mensal e arredondamento único do total. O boletim agora explicita DR0095, lançamento por valor, mês, ano e total por militar. Não foi feita certificação de toda a base histórica de NR0095; a base monetária continua sendo o valor salvo da competência ou o cadastro quando não há valor salvo.

## Ficha e residência

Na aba Rotas / Endereço: anexar PDF, PNG ou JPG; abrir; remover vínculo; emitir declaração. O comprovante é opcional, fica copiado no perfil por militar e acompanha a ficha em páginas próprias. Todos os lados de um PDF multipágina são incluídos. Um vínculo com arquivo ausente interrompe a geração e pede correção, em vez de omitir silenciosamente o anexo.

Os botões Ficha da rota e Declaração de residência / imprimir ficam no topo de Rotas / Endereço. A declaração consulta novamente o cadastro do militar selecionado e traz nome, CPF e endereço. Somente Cidade / UF e Data são solicitadas na tela. A data usa calendário e começa no dia atual; Cidade / UF é salva automaticamente no perfil para as próximas declarações. Os demais campos ficam em branco para preenchimento à mão. A prévia é A4, com assinatura do responsável pelo imóvel. Imprimir declaração abre a seleção de impressora. A ação de impressão é do usuário; não foi enviada nenhuma impressão durante a validação.

## Validação

- 63 testes de transporte, contracheques e conferência aprovados.
- PDFs reais: 59 publicações lidas entre junho e agosto; 24 com eventos aplicáveis a julho; cobertura de 31 dias, 13 substituições e 599 serviços únicos por militar/dia.
- 16 ocorrências de grafia/identidade ficam pendentes. A prévia compara o estado salvo de julho com a recontagem, sem escrever no banco de produção.
- Ficha sem comprovante e ficha com comprovante de duas páginas convertidas em DOCX/PDF e verificadas visualmente. A declaração foi renderizada em uma página A4.
- Executável compilado separadamente. A instância aberta pelo usuário não foi encerrada.

Arquivos de revisão: revisao-julho/REVISAO_DA_JULHO_2026.html e DA_JULHO_2026_PREVIA.txt, junto ao executável entregue.

## Ajuste de 07/09/2026

Conferidos o botão na aba Rotas / Endereço, a data de hoje, a alteração da data na prévia, a persistência de Cidade / UF entre instâncias e os dados do militar no documento. A declaração simplificada foi renderizada e continua em uma página A4.
