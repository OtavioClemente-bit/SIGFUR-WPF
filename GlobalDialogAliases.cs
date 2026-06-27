// Faz com que MessageBox.Show(...) use a janela visual própria do SIGFUR.
// O SigfurDialog mantém assinaturas compatíveis com os usos atuais e só cai
// no MessageBox nativo em falha extrema de inicialização/renderização.
global using MessageBox = SIGFUR.Wpf.Services.SigfurDialog;
