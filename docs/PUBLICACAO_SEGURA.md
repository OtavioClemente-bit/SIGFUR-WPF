# Checklist de publicação segura

Use este checklist antes de publicar qualquer atualização do SIGFUR WPF no GitHub.

## Remover sempre

- `bin/`
- `obj/`
- `.venv/`
- `tmp/`
- `PUBLICADO/`
- `__pycache__/`
- `*_wpftmp.csproj`
- `*.pyc`
- `*.db`
- `*.sqlite`
- `*.sqlite3`
- `*.pdf`
- `*.docx`
- `*.odt`
- `*.xlsm`
- `*.zip`

## Conferir manualmente

- Se há nomes reais
- Se há CPF, PREC-CP, identidade ou dados bancários
- Se há contracheques, boletins ou PDFs internos
- Se há senhas, tokens, usuários ou caminhos locais

## Publicar

Somente depois de conferir que o repositório possui apenas código-fonte, assets seguros e documentação.
