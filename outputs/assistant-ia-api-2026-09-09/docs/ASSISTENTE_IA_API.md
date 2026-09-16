# Assistente SIGFUR — IA por API

O assistente usa exclusivamente a API da OpenAI para análise e redação. Não instala modelos, não inicia servidor de IA neste computador e não oferece modo automático ou local. Os leitores de documentos, os índices, os cálculos e os controles do SIGFUR continuam no programa.

## Configuração

Abra **Configurar assistente**, informe a chave e use **Testar conexão**. A chave fica no Gerenciador de Credenciais do Windows. O teste verifica a autenticação; não executa uma auditoria completa nem confirma disponibilidade de todas as ferramentas do modelo.

O modelo inicial é `gpt-5.4-mini`, com raciocínio `medium` e até 6.000 tokens de saída. Modelo, raciocínio, contexto, etapas e orçamento podem ser ajustados. Configurações anteriores preservam modelo, orçamento e preferências; campos da antiga IA local são ignorados.

A API exige acesso HTTPS a `api.openai.com` e saldo na conta da API. Esta atualização não remove bloqueios da intranet. A chave pode ser salva mesmo quando a conexão não está disponível.

## Operação

- **Boletim → Auditar com IA:** revisa a versão integral do texto, solicita evidência no manual e na legislação e salva um parecer com identificação da versão auditada. Alterações posteriores exigem nova auditoria.
- **Conferência de pagamento → Conferir → IA: selecionados / IA: filtro atual:** cruza os dados extraídos de publicações e contracheques em lotes. Cada item deve ter uma resposta identificada. O parecer mostra alcance, itens sem resposta e interrupções. A IA não marca verificação humana nem altera valores.
- **Exercícios anteriores:** os botões de IA preparam motivo, materialização do direito e justificativa do não pagamento usando os campos e lançamentos informados. A prévia permite editar, aplicar ou descartar. Textos de exemplo antigos não são considerados prova do processo.
- **Assistente → Pendências:** reúne lembretes ativos, controles de pagamento e processos de EA não pagos. **Organizar com IA** consulta a fila e propõe uma ordem de trabalho. **Abrir módulo do item** permite resolver a pendência na origem.
- **Alertas:** um aviso na janela principal mostra prazos próximos, vencidos e urgências cadastradas. A antecedência é configurável. **Ciente por hoje** dispensa os alertas atuais naquele dia. O monitor funciona enquanto o SIGFUR está aberto; a verificação da fila não usa API nem tokens.
- **Criar lembrete pelo chat:** prepara um formulário para revisão. O lembrete passa a existir após o operador salvar esse formulário.

## Fontes e memória

O manual SIPPES e a legislação usam o índice documental existente. A busca recupera páginas relevantes, trechos, nome do arquivo, versão e identificação SHA-256. As posições de consulta ficam em cache persistente, limitado a 128 entradas e 512 KB. O cache não guarda cópias integrais dos manuais nem perguntas em texto puro.

O conteúdo selecionado é verificado antes de reutilizar referências. Fontes alteradas, removidas ou ilegíveis não sustentam conclusões. A biblioteca é reindexada incrementalmente; arquivos substituídos mantendo tamanho/data e com hash diferente precisam de reindexação no módulo Legislação.

O limite de contexto é aplicado ao pedido, histórico e etapas internas. Um pedido integral que não caiba é recusado antes do envio, com orientação para dividi-lo. Resultados de ferramentas limitados são identificados como parciais. PDFs digitalizados sem texto legível podem exigir OCR ou revisão documental.

A pesquisa web usa temas administrativos públicos padronizados e domínios oficiais, em chamada separada da conversa. Ela não recebe nomes, contracheques, anexos ou consultas livres com fatos privados. A análise principal pela API recebe os dados operacionais necessários; a opção de ocultação reduz identificadores, mas não equivale a anonimização completa.

Norma atual, norma vigente na época do fato e orientação técnica são distinguidas nas instruções. A ausência de fonte deve ser apontada como lacuna; referências recuperadas não certificam automaticamente a legalidade de um pagamento.

## Consumo e registros

As respostas e ferramentas usam a Responses API com `store: false`. O histórico do SIGFUR, quando habilitado, permanece protegido por DPAPI no Windows. A opção `store: false` não substitui as políticas de retenção do provedor.

Cada resposta de API recebida tem seu uso contabilizado antes de processar resultados. Pesquisas web entram na estimativa. O orçamento é reavaliado antes de cada chamada e as solicitações de IA são serializadas. A estimativa não é uma fatura: preços cadastrados, câmbio, descontos de cache, modelo personalizado e chamadas interrompidas sem retorno de uso podem produzir diferenças. Uma chamada já iniciada pode ultrapassar o saldo interno restante.

Pareceres ficam em `documentos_gerados/Assistente_SIGFUR/Revisoes` conforme a pasta de dados do perfil. Use o caminho exibido ao concluir a auditoria. Resultados parciais são preservados com indicação de cobertura; nunca são tratados como conferência integral concluída.

## Validação técnica

Os testes usam dados sintéticos, respostas de API simuladas e diretórios temporários. Não enviam contracheques reais nem geram cobrança de API.

```powershell
dotnet build SIGFUR.Wpf.csproj
dotnet run --project Tests/AssistantKnowledgeRegression/AssistantKnowledgeRegression.csproj
dotnet run --project Tests/AssistantProfessionalRegression/AssistantProfessionalRegression.csproj
```

Os testes verificam cache e invalidação, contexto, protocolo de ferramentas, resposta incompleta, cancelamento, privacidade das consultas web, cobertura dos lotes, campos sem valores conhecidos, textos de exemplo de EA, prazos e migração de configuração. Não substituem uma homologação com amostras reais e API acessível.

Referências técnicas: [Responses e ferramentas](https://developers.openai.com/api/docs/guides/function-calling), [pesquisa web](https://developers.openai.com/api/docs/guides/tools-web-search).
