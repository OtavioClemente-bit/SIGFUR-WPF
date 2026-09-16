# Preenchimento e conferência de boletins

## Comportamento

- A regra é vinculada por nome exato ou alias único. Similaridade textual continua disponível na pesquisa, mas não escolhe regras obrigatórias de outro assunto.
- A troca de modelo invalida a prévia, a conferência e a auditoria assíncrona anterior. Os controles gerais e individuais são reconstruídos. Editar o modelo recarrega a regra.
- Os campos individuais são montados após o fim da construção do formulário; modelos sem campos individuais limpam os controles antigos.
- Tokens não resolvidos e ausência de militares bloqueiam copiar, salvar e enviar, inclusive quando não existe regra cadastrada. Datas inválidas, intervalos invertidos e quantidades inválidas presentes no modelo também geram impedimento.
- Valores individuais e campos condicionais resolvidos pelo renderizador não são cobrados novamente como campos gerais.
- Escolher uma referência no campo aplica ao campo/militar em edição. A ação geral permanece no menu de ferramentas. Escolher um ADT não apaga as chaves de BI já preenchidas e vice-versa.
- Referência padrão: `BI Nr 26, de 14 ABR 26, da 4ª Cia PE` ou `Adt Furr Nr 26, de 14 ABR 26, da 4ª Cia PE`. BAR é preservado quando informado.
- O relatório apresenta impedimentos, avisos e verificações concluídas; não apresenta nota percentual como certificação de conformidade. Orientações e verificações concluídas ficam em seções expansíveis.

## Manual consultado

Manual Técnico SIPPES de 17/07/2026. O PDF fornecido em Downloads é idêntico ao recurso incorporado (SHA256 `35B3A66CF4AC20CE76D06651A1A8E56C0707A41D459CDB5389E66AAB21939FDE`).

Orientações contextualizadas: pensão e vigência (p. 84); pré-escolar e vínculo de dependentes (p. 125); transporte, natureza da rubrica e competência antecipada (pp. 128–131); alimentação e natureza NR/AR/FR/DR (pp. 132–133). A base de regras anterior não foi promovida a uma certificação integral do manual: direitos, documentos de origem e assuntos sem regra continuam exigindo conferência do operador.

## Verificação

`BulletinComplianceTests`: vínculo exato/ambíguo, modelo sem regra, seleção vazia, campo removido, valor individual, datas inválidas/invertidas e formato BI/ADT.

Probe WPF em `tmp/bulletin-refactor-review/probe`: usa diretório isolado e militar fictício; valida a construção dos campos individuais, mudança para modelo sem campos e ausência de exigência antiga; renderiza preenchimento e conferência. Não envia publicação ao SisBol.
