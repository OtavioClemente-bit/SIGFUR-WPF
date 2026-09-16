# Assistente SIGFUR Local e Offline

O Assistente Local executa um modelo GGUF no próprio computador por meio do `llama-server` do projeto `llama.cpp`. Conversas, anexos, legislação e dados consultados permanecem na máquina. A comunicação ocorre exclusivamente em `http://127.0.0.1` e não usa proxy, DNS ou internet.

## Arquitetura de segurança

- O modelo não acessa diretamente o banco de dados nem navega pelo disco.
- Consultas passam pelas ferramentas controladas do `AssistantDataService`.
- Dados pessoais permanecem ocultos, salvo quando o operador pede explicitamente um campo permitido.
- Conta e agência bancária não são disponibilizadas ao assistente.
- Abertura de arquivo, carteira, rota e impressão continuam dependendo de clique/confirmacão do operador.
- Respostas jurídicas devem citar documento e página recuperados da biblioteca offline.
- O `llama-server` é iniciado em `127.0.0.1`, nunca em `0.0.0.0`.
- Quando o histórico está habilitado, ele é protegido pelo DPAPI do usuário atual do Windows; o formato antigo em texto puro é migrado automaticamente.

## Arquivos necessários

1. Distribuição oficial do `llama.cpp` para Windows x64, incluindo `llama-server.exe` e todas as DLLs do mesmo pacote.
2. Um modelo de instruções no formato `.gguf`, com bom suporte a português e chamadas de ferramentas.

Perfil recomendado:

| Memória RAM disponível | Modelo sugerido | Quantização | Observação |
|---|---|---|---|
| 8 GB | modelo de 3B a 4B | Q4_K_M | Funciona, mas tem menor precisão para textos jurídicos complexos. |
| 16 GB | Qwen3 8B | Q4_K_M | Perfil recomendado para o SIGFUR. |
| 24–32 GB | Qwen3 14B | Q4_K_M | Melhor redação e interpretação, com respostas mais lentas em CPU. |

Para computadores com 8 GB, use `Qwen3-4B-GGUF / Q4_K_M`. O perfil `Qwen3-8B-GGUF / Q4_K_M` fica reservado a máquinas com pelo menos 16 GB. Ambos são multilíngues, Apache 2.0 e suportam uso de ferramentas. Use sempre a distribuição oficial do modelo e registre o SHA-256 do arquivo levado para a máquina institucional.

Fontes oficiais:

- llama.cpp e servidor local: https://github.com/ggml-org/llama.cpp
- API do llama-server: https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md
- Qwen3-8B-GGUF: https://huggingface.co/Qwen/Qwen3-8B-GGUF
- Qwen3-4B-GGUF: https://huggingface.co/Qwen/Qwen3-4B-GGUF

## Instalação offline

Faça os downloads em uma máquina autorizada com internet e transfira os arquivos pelo meio permitido pela organização.

1. Abra o SIGFUR e acesse `Assistente > Configurar assistente`.
2. Clique em `Abrir pasta da IA local`.
3. Extraia a distribuição completa do llama.cpp em:
   `%LOCALAPPDATA%\SIGFUR\assistente_local\runtime`
4. Copie o modelo `.gguf` para:
   `%LOCALAPPDATA%\SIGFUR\assistente_local\modelos`
5. Na configuração, selecione `llama-server.exe` e o arquivo `.gguf`.
6. Mantenha o endpoint `http://127.0.0.1:8080/v1`.
7. Comece com:
   - contexto: `8192` em máquina com 8 GB de RAM;
   - threads: `4` como ponto inicial em CPU com 8 GB;
   - camadas na GPU: `0` para máxima compatibilidade;
   - temperatura: `0,15`;
   - timeout: `600` segundos.
8. Clique em `Testar IA local` e aguarde a primeira carga. Ela pode levar de dezenas de segundos a alguns minutos.
9. Se o teste funcionar, escolha `Somente local / offline` e salve.

Não copie apenas o `llama-server.exe`: as DLLs distribuídas junto dele são necessárias. Não renomeie um arquivo de outro formato para `.gguf`.

## Preparação das fontes

O modelo não “decora” a legislação do quartel. A resposta profissional é produzida por recuperação de evidências (RAG):

1. Importe PDFs, DOCX, ODT, HTML ou TXT no módulo `Legislação`.
2. Execute a indexação do módulo após adicionar ou atualizar documentos.
3. Importe/indexe boletins nos módulos `Boletim Inteligente`, `Índice por Pessoa` e `Aditamento do Furriel`.
4. Mantenha os documentos dos militares e contracheques nas pastas oficiais gerenciadas pelo SIGFUR.

Quando uma fonte é encontrada, a conversa mostra um link seguro com título, BI/ADT ou página. Para PDFs jurídicos, o SIGFUR tenta abrir diretamente na página citada pelo Microsoft Edge.

## Roteamento profissional de consultas

A partir da versão 6.2.1, o assistente classifica a pergunta antes de chamar o modelo. Conversa e redação geral não carregam ferramentas. Legislação, pessoal, férias, boletins, pagamento, contracheques, lembretes, escala e documentos recebem somente as ferramentas do respectivo domínio, limitadas a seis por solicitação. A classificação aparece durante o processamento e no resumo de fontes.

Isso evita que uma pergunta comum seja interpretada como nome de militar e reduz substancialmente a quantidade de tokens processada em CPU. O log registra domínio, ferramentas selecionadas, histórico enviado, tokens e duração de cada rodada.

## Diagnóstico

O log do mecanismo local fica em:
`%LOCALAPPDATA%\SIGFUR\logs\assistente_local_llama.log`

Problemas comuns:

- encerramento imediato: DLLs do pacote llama.cpp ausentes ou modelo incompatível;
- memória insuficiente: use modelo/quantização menor ou reduza o contexto;
- resposta muito lenta: reduza o contexto, use modelo 4B ou configure uma compilação com GPU compatível;
- roteamento inesperado: confira no resumo da resposta a linha `Roteamento:` e consulte o log para ver as ferramentas realmente enviadas;
- ferramentas não chamadas: confirme que o modelo é de instruções, suporta tool calling e que o servidor foi iniciado com `--jinja`;
- nenhuma legislação localizada: reindexe a biblioteca e confira se o PDF possui texto pesquisável/OCR.

O modo offline não torna uma resposta automaticamente correta. Para pagamento, publicação ou ato oficial, confira as páginas citadas antes de adotar a orientação.
