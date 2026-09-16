# Lista de militares — 7 de setembro de 2026

A relação usa duas áreas: tabela e painel de detalhes. Busca e filtros ficam no topo. O botão Detalhes permite ampliar a tabela. Em janelas menores, os dados laterais rolam e Editar/Abrir carteira continuam acessíveis.

Ações frequentes ficam próximas à tabela; exportação, ferramentas, visualização e listas possuem menus próprios. CPF sem pontuação foi removido desta tela. Copiar CPF, copiar dados e copiar no padrão da relação continuam disponíveis. Layout essencial restaura uma seleção enxuta de colunas; as demais continuam em Escolher colunas. Configurações anteriores de colunas são respeitadas.

## Pesquisa e desempenho

- Índice em memória com normalização de acentos e busca por todos os termos, em qualquer ordem.
- CPF aceita pontuação ou apenas números. Identificadores são indexados separadamente para impedir correspondência artificial entre o final de um campo e o começo de outro.
- A pesquisa tem intervalo de 180 ms após a digitação. Resultados idênticos preservam a coleção e evitam reconstruir as linhas.
- O texto de orientação da busca aparece apenas enquanto o campo está vazio e sem foco. Ao clicar ou usar Ctrl+F, ele desaparece antes da digitação para não ficar sobreposto ao cursor.
- Leituras do SQLite ocorrem fora da thread da interface, inclusive quando o provedor executa métodos assíncronos de forma síncrona.
- Virtualização de linhas e colunas preservada. Marcação e favorito notificam suas propriedades sem atualizar toda a grade.
- A abertura não percorre caminhos de fotos em disco/rede para calcular uma estatística secundária.

## Seleção

Marcar e selecionar para visualizar continuam independentes. Marcações sobrevivem à pesquisa, atualização do efetivo e atualização individual do cadastro durante a sessão. Marcar todos os visíveis afeta somente o resultado atual. Limpar filtros preserva marcações, lista ativa e ordem. Esc limpa as marcações; dentro de um combo aberto, continua fechando o combo.

## Documentos rápidos do militar selecionado

O painel lateral reúne Editar, Abrir carteira, Abrir último contracheque e Abrir última ficha financeira.

- Contracheque: escolhe a maior competência `MM/AAAA` realmente salva para o militar. A data de modificação do PDF só desempata arquivos da mesma competência. Se nenhum contracheque local com competência confiável existir, usa as credenciais protegidas salvas na Central, entra no CPEx e tenta a competência atual e até 11 competências anteriores; abre o PDF após baixar.
- Ficha financeira: escolhe o maior ano realmente salvo para o militar. Se nenhuma ficha local com ano confiável existir, usa as credenciais protegidas salvas, entra no CPEx e baixa a ficha do ano corrente; abre o PDF após baixar.
- Os arquivos são vinculados ao militar por CPF, PREC-CP, identidade ou nome presentes na estrutura oficial de armazenamento. Um arquivo sem identidade suficiente não é escolhido.
- Durante cada operação, o botão do painel e a ação equivalente do menu ficam desativados, evitando dois navegadores/downloads concorrentes.

Se as credenciais ainda não estiverem salvas, o SIGFUR informa que é necessário cadastrá-las na Central de Contracheques. O teste automatizado não acessa o CPEx nem altera arquivos reais.

## Verificação

Os testes selecionados cobrem pesquisa/seleção e a escolha do documento local mais recente por competência/ano. O ensaio WPF usou 177 registros e uma cópia isolada do banco: carregamento de aproximadamente 1 segundo, retorno à interface durante a leitura e preservação de seleção/marcação na recarga. Renderizações revisadas em 1440×840 e 960×600, com os quatro botões acessíveis. Os tempos são observações locais, não garantias para outras máquinas.

Os testes não modificaram o banco de produção. A compilação conserva avisos anteriores, inclusive NU1903 da dependência SQLitePCLRaw.lib.e_sqlite3; a atualização de dependências não fez parte desta refatoração.
