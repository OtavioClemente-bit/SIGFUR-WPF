# Perfil e carregamento — 7 de setembro de 2026

## Entrada por perfil

A tela inicial usa a identidade institucional do SIGFUR e prioriza a entrada no perfil. O perfil usado mais recentemente vem selecionado. Ao trocar de perfil, a senha digitada é apagada e os caminhos/último backup são atualizados somente se aquele perfil ainda estiver selecionado.

- `Enter` valida a senha e entra no perfil selecionado.
- `Esc` cancela e fecha a aplicação.
- `Entrar e restaurar backup` continua sendo uma ação separada e explícita.
- Enquanto a senha é validada ou um backup é restaurado, todos os comandos de entrada ficam bloqueados e o rodapé mostra o andamento.
- Entrar sem perfil abre diretamente os dados deste computador, sem uma confirmação redundante.

## Remoção de perfil

`Remover` desfaz somente o vínculo do perfil neste computador. A confirmação mostra o nome exato do perfil e informa que a pasta de dados local, a pasta sincronizada, o arquivo `profile.sigfurprofile` e os backups serão preservados. O perfil pode ser conectado novamente depois.

A remoção exige correspondência do nome e da pasta sincronizada. Isso impede que uma entrada com o mesmo nome e outro caminho seja removida por engano. A gravação da lista usa o mecanismo atômico do armazenamento JSON.

## Carregamento

A abertura do programa e os carregamentos internos compartilham cabeçalho institucional, marca SIGFUR, tipografia, cartões, cores de progresso e mensagens de estado.

- A abertura apresenta quatro estágios: Preferências, Dados, Serviços e Painel.
- O número da versão exibido vem do próprio executável.
- O progresso continua suave e nunca volta para trás.
- O overlay interno agora captura os cliques enquanto está visível, impedindo comandos duplicados na janela que está processando.
- O conteúdo atrás do overlay permanece escurecido e legível como contexto, com o andamento em primeiro plano.

## Verificação

Foram revisadas renderizações da tela de perfil em 980×700 e 760×620, da abertura em 900×560 e do overlay em 1200×720. Quinze testes selecionados passaram, incluindo remoção isolada de perfil, preservação dos arquivos locais e sincronizados, busca de militares e escolha dos documentos financeiros mais recentes. Nenhum perfil ou backup real foi removido durante os testes.
