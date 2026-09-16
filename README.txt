Hotfix - compilacao da migracao de contracheques

Corrige os erros CS1009/CS1010 provocados por strings com caminho Documentos\SIGFUR\Contracheques e texto multilinha em:
- Views/Military/MilitaryListWindow.xaml.cs
- Views/Military/PaystubCenterWindow.xaml.cs

Ajuste aplicado:
- mensagens multilinha convertidas para string verbatim (@"...");
- string com caminho de pasta no resumo tambem convertida para string verbatim;
- mantidas as alteracoes anteriores de cores do Listar Militares e migracao segura de Documentos para AppData.
