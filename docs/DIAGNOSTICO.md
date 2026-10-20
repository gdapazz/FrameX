# Diagnóstico

## Onde olhar primeiro

1. **Página Hardware → Provedores:** o estado de cada provedor (LibreHardwareMonitor, Windows) e de cada sensor, com o motivo de indisponibilidade.
2. **Barra superior:** estado do jogo, da captura de quadros (PresentMon) e do modo (padrão/administrador).
3. **Sobre → Ambiente:** versão do .NET e do Windows, privilégios, pastas de dados e de logs, com botões para abri-las.
4. **Logs:** `%LOCALAPPDATA%\FRAMEX\logs\framex-AAAAMMDD.jsonl`, um evento JSON por linha:
   ```powershell
   Get-Content "$env:LOCALAPPDATA\FRAMEX\logs\framex-$(Get-Date -f yyyyMMdd).jsonl" -Encoding UTF8 |
     ConvertFrom-Json | Where-Object { $_.'@l' -in 'Warning','Error','Fatal' } |
     Select-Object '@t','@l','@mt','@x'
   ```
   Os logs não contêm dados de rede porque nada é enviado. Antes de compartilhar um log, verifique se ele contém caminhos de jogos.

## Problemas comuns

| Sintoma | Causa provável | Solução |
|---|---|---|
| FPS “—” com o jogo aberto | PresentMon ausente ou sem permissão ETW | Veja [EXECUCAO.md](EXECUCAO.md#presentmon-fps-e-frametime). O estado aparece na barra superior. |
| “Aguardando quadros” por muito tempo | O jogo usa uma API que não gera eventos de Present para o processo detectado (por exemplo, a janela pertence a outro processo); ou um launcher foi associado no lugar do jogo | Associe o executável do jogo, não o do launcher. O FRAMEX avisa após 5 s sem quadros. |
| Temperatura/clock/consumo da CPU “—” | O driver do LibreHardwareMonitor exige administrador | **Configurações → Reiniciar como administrador**. Alguns processadores ou placas não expõem o sensor de forma alguma. |
| Sensor de GPU ausente | Driver antigo ou GPU sem suporte no LibreHardwareMonitor | Atualize o driver de vídeo. A página Hardware mostra o que foi detectado. |
| Overlay não aparece sobre o jogo | Tela cheia exclusiva | Use janela sem bordas. |
| Atalho global não funciona | Combinação usada por outro programa | O aviso aparece no topo da janela. Escolha outra combinação em **Configurações → Atalhos**. |
| “Sessão incompleta” no histórico | O FRAMEX ou o Windows foi encerrado durante a sessão | Normal: tudo o que foi gravado até o último lote (≤ 5 s) é mantido. |
| Aviso de banco recuperado | Banco corrompido na abertura | O arquivo original foi preservado como `framex.db.corrompido-<data>` e um banco novo foi criado. |
| Processo `PresentMon` após fechar o FRAMEX | Não deve ocorrer: o processo roda em um Job Object com encerramento automático | Se ocorrer, registre o log. Para encerrar a sessão ETW: `PresentMon --terminate_existing_session --session_name FRAMEX_Capture`. |

## Verificação rápida da instalação

```powershell
./scripts/test.ps1 -UiSmoke
```

Renderiza todas as telas com sensores reais em uma pasta temporária e falha se houver erros de binding ou exceções.

## Reiniciar do zero

Feche o FRAMEX e renomeie ou apague `%LOCALAPPDATA%\FRAMEX`. Um backup do banco é criado automaticamente antes de cada migração de esquema (`framex.db.v<N>.bak`).
