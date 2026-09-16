# Assistente SIGFUR — IA por API

O assistente usa exclusivamente a API da OpenAI para análise e redação. Não instala modelos, não inicia servidor de IA neste computador e não oferece modo automático ou local. Os leitores de documentos, os índices, os cálculos e os controles do SIGFUR continuam no programa.

## Configuração

Abra **Configurar assistente**, informe a chave e use **Testar conexão**. A chave fica no Gerenciador de Credenciais do Windows. O teste verifica a autenticação; não executa uma auditoria completa nem confirma disponibilidade de todas as ferramentas do modelo.

O modelo inicial e recomendado é `gpt-5.6-luna`. Configurações anteriores preservam o modelo escolhido manualmente, orçamento e preferências. O roteador escolhe esforço `low` para reescrita, classificação e resgates curtos e permite `medium` nas associações de pagamento realmente ambíguas; os limites gerais continuam configuráveis.

A API exige acesso HTTPS a `api.openai.com` e saldo na conta da API. Esta atualização não remove bloqueios da intranet. A chave pode ser salva mesmo quando a conexão não está disponível.

## Operação

- **Conferência de pagamento → ✦ Auditoria mensal IA:** inventaria boletins, páginas e contracheques da competência; executa o parser determinístico; envia ao Luna somente páginas ambíguas e divergências; cruza boletim → contracheque e mudanças relevantes contracheque → boletim; e produz um ledger por militar. Antes das chamadas, mostra blocos, itens, tokens, custo e orçamento restante. Falha documental gera `AUDITORIA PARCIAL`, nunca aprovação silenciosa.
- **Conferência de pagamento → IA: selecionados / IA: filtro atual:** mantém a revisão pontual, agora com JSON estruturado e validação programática de todos os `item_id`, sem regex sobre relatório livre.
- **Boletim Inteligente → ✦ Reanalisar pendentes com IA:** usa a IA somente nas páginas relevantes que o parser não estruturou. A origem aparece como `IA — revisar`; o achado nunca nasce conferido.
- **Boletim → Auditar com IA:** envia uma evidência local estruturada contendo texto final completo, formulário, pessoas, rubricas, validações e alertas. O retorno separa erro objetivo, alerta, ausência de evidência, sugestão e inconclusivo.
- **Exercícios anteriores:** os botões de IA preparam motivo, materialização do direito e justificativa do não pagamento usando os campos e lançamentos informados. A prévia permite editar, aplicar ou descartar. Textos de exemplo antigos não são considerados prova do processo.
- **Assistente → Pendências:** reúne lembretes ativos, controles de pagamento e processos de EA não pagos. **Organizar com IA** consulta a fila e propõe uma ordem de trabalho. **Abrir módulo do item** permite resolver a pendência na origem.
- **Alertas:** um aviso na janela principal mostra prazos próximos, vencidos e urgências cadastradas. A antecedência é configurável. **Ciente por hoje** dispensa os alertas atuais naquele dia. O monitor funciona enquanto o SIGFUR está aberto; a verificação da fila não usa API nem tokens.
- **Criar lembrete pelo chat:** prepara um formulário para revisão. O lembrete passa a existir após o operador salvar esse formulário.

## Fontes e memória

O manual SIPPES e a legislação usam o índice documental existente. A busca recupera páginas relevantes, trechos, nome do arquivo, versão e identificação SHA-256. As posições de consulta ficam em cache persistente, limitado a 128 entradas e 512 KB. O cache não guarda cópias integrais dos manuais nem perguntas em texto puro.

O conteúdo selecionado é verificado antes de reutilizar referências. Fontes alteradas, removidas ou ilegíveis não sustentam conclusões. A biblioteca é reindexada incrementalmente; arquivos substituídos mantendo tamanho/data e com hash diferente precisam de reindexação no módulo Legislação.

O limite de contexto é aplicado ao pedido, histórico e etapas internas. Um pedido integral que não caiba é recusado antes do envio, com orientação para dividi-lo. Resultados de ferramentas limitados são identificados como parciais. PDFs digitalizados sem texto legível podem exigir OCR ou revisão documental.

Interpretações de páginas e lotes usam cache local derivado do SHA-256 do arquivo, página/bloco, versão do parser, versão do prompt e modelo. Arquivo, parser, prompt ou modelo alterado invalida a entrada. As chamadas também usam `prompt_cache_key` por fluxo, com regras estáveis antes dos dados variáveis.

A pesquisa web usa temas administrativos públicos padronizados e domínios oficiais, em chamada separada da conversa. Ela não recebe nomes, contracheques, anexos ou consultas livres com fatos privados. A análise principal pela API recebe os dados operacionais necessários; a opção de ocultação reduz identificadores, mas não equivale a anonimização completa.

Norma atual, norma vigente na época do fato e orientação técnica são distinguidas nas instruções. A ausência de fonte deve ser apontada como lacuna; referências recuperadas não certificam automaticamente a legalidade de um pagamento.

## Consumo e registros

As respostas e ferramentas usam a Responses API com `store: false`. O histórico do SIGFUR, quando habilitado, permanece protegido por DPAPI no Windows. A opção `store: false` não substitui as políticas de retenção do provedor.

Cada resposta de API recebida tem seu uso contabilizado antes de processar resultados. Para o Luna, o registro separa entrada não cacheada, `usage.input_tokens_details.cached_tokens`, saída e ferramentas pagas. Pesquisas web entram na estimativa. O orçamento é reavaliado antes de cada chamada, e a auditoria mensal também bloqueia a confirmação quando seu custo máximo estimado excede o saldo interno. A estimativa não é uma fatura: câmbio, preços futuros, modelo personalizado e chamadas interrompidas sem retorno de uso podem produzir diferenças.

Pareceres ficam em `documentos_gerados/Assistente_SIGFUR/Revisoes` conforme a pasta de dados do perfil. Use o caminho exibido ao concluir a auditoria. Resultados parciais são preservados com indicação de cobertura; nunca são tratados como conferência integral concluída.

## Validação técnica

Os testes usam dados sintéticos, respostas de API simuladas e diretórios temporários. Não enviam contracheques reais nem geram cobrança de API.

```powershell
dotnet build SIGFUR.Wpf.csproj
dotnet run --project Tests/AssistantKnowledgeRegression/AssistantKnowledgeRegression.csproj
dotnet run --project Tests/AssistantProfessionalRegression/AssistantProfessionalRegression.csproj
```

Os testes verificam cache e invalidação por conteúdo, roteamento de ferramentas, custo normal/cacheado do Luna, Structured Outputs, rejeição de resposta truncada ou IDs incompletos, recuperação de publicação não reconhecida, auditoria nos dois sentidos, rubrica recorrente, cobertura parcial, cancelamento e impossibilidade de aprovação automática. Usam fixtures e não substituem homologação com PDFs reais e API acessível.

Referências técnicas: [GPT-5.6 Luna](https://developers.openai.com/api/docs/models/gpt-5.6-luna), [Responses API](https://developers.openai.com/api/reference/resources/responses/methods/create), [Responses e ferramentas](https://developers.openai.com/api/docs/guides/function-calling), [pesquisa web](https://developers.openai.com/api/docs/guides/tools-web-search).
