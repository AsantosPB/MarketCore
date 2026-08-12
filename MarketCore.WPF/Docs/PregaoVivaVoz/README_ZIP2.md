# 🎙️ Pregão Viva Voz - ZIP 2 - Motor + Simulador

## 📦 O que vem neste ZIP

Este ZIP 2 adiciona o **motor de detecção e reprodução** por cima do ZIP 1 que você já instalou.

### ✅ Arquivos NOVOS (5)

1. **Services/PregaoVivaVoz/FraseBuilderService.cs**
   - Monta as frases: "Goldman tomou" + "500" = arquivos WAV
   - Aplica regra de arredondamento (ponto de corte 50)
   - Converte números para texto ("500" → "quinhentos")

2. **Services/PregaoVivaVoz/AudioPlaybackService.cs**
   - Fila FIFO de reprodução
   - Usa NAudio quando disponível (adicione o pacote NuGet)
   - **Se WAV não existir → LOGA no console** (modo teste sem áudio)

3. **Services/PregaoVivaVoz/DetectorRajadaService.cs**
   - Buffer circular por player em **milissegundos**
   - Detecta INÍCIO de rajada (N agressões em X ms)
   - Detecta FIM (Z ms sem nova agressão) - o gatilho mais valioso!

4. **Services/PregaoVivaVoz/PregaoVivaVozEngine.cs**
   - Motor principal (Event Bridge pattern)
   - Métodos públicos: `ProcessarAgressao()`, `ProcessarBook()`, `ProcessarTrade()`
   - Aplica todos os filtros do usuário

5. **Services/PregaoVivaVoz/EventoSimulador.cs**
   - Simulador de eventos aleatórios pra testes
   - Gera agressões, book e rajadas simulando o WIN real
   - Permite testar SEM precisar do pregão ao vivo

### ✅ Arquivos ATUALIZADOS (2)

1. **ViewModels/PregaoVivaVoz/PregaoVivaVozViewModel.cs**
   - Adiciona controle do motor + simulador
   - Log em tempo real (últimos 100 eventos)
   - Estatísticas ao vivo (processados, narrados, rajadas)

2. **Views/PregaoVivaVoz/PregaoVivaVozWindow.xaml**
   - Novo bloco superior: **⚡ Motor de Detecção + Simulador**
   - 3 cards de estatísticas grandes
   - Log rolando em tempo real

## 🚀 Instalação (5 minutos)

### Passo 1: Backup do estado atual (opcional mas recomendado)

Antes de extrair, faça uma cópia de segurança da pasta MarketCore.WPF pra caso queira reverter.

### Passo 2: Extrair o ZIP 2

1. Baixe o ZIP 2 aqui do chat
2. Vá em `C:\Users\Anderson\Downloads\MarketCore\`
3. Clique com botão direito no ZIP → **"Extrair aqui"**
4. Windows perguntará se quer substituir/mesclar → **"Sim para todos"**

Os arquivos vão SUBSTITUIR os do ZIP 1 (as versões novas do ViewModel + Window).

### Passo 3: Instalar NAudio (opcional mas recomendado)

**Sem NAudio:** o sistema funciona 100% mas em modo LOG (imprime no console)
**Com NAudio:** o sistema toca os WAVs quando existirem

Pra instalar:

1. Abra o Visual Studio
2. Na **Solution Explorer**, clique com botão direito no projeto **MarketCore.WPF**
3. Escolha **"Manage NuGet Packages..."**
4. Aba **Browse**, procure por **NAudio**
5. Instale a versão mais recente (2.2.1+)
6. Em `AudioPlaybackService.cs`, no topo do arquivo, adicione a constante de build:

Se você conseguiu instalar NAudio, edite `MarketCore.WPF.csproj` e adicione dentro do `<PropertyGroup>`:

```xml
<DefineConstants>$(DefineConstants);NAUDIO</DefineConstants>
```

**Ou** faça o Cowork fazer isso pra você. Se não fizer nada, o sistema continua funcionando em modo LOG.

### Passo 4: Compilar

```
Build → Rebuild Solution (Ctrl+Shift+B)
```

Deve compilar sem erros (só warnings do código legado).

### Passo 5: Testar!

1. Abra o MarketCore
2. Clique no botão 🎙️ na barra superior
3. A janela do Pregão Viva Voz abre com um NOVO bloco no topo: **"⚡ Motor de Detecção + Simulador"**

## 🎯 Como testar (a parte MAIS DIVERTIDA)

### Teste 1: Motor sozinho

1. **Ative alguns players** clicando nas pastilhas (Goldman, JPM, Merrill)
2. **Ajuste os limites** do bloco Agressão pra valores baixos (ex: Goldman=10, JPM=10)
3. **Ative Rajada** pra esses players
4. Clique no botão **"▶ Iniciar motor"**
5. Você verá no log: `🟢 Motor iniciado · aguardando eventos`
6. Nada mais acontece (esperando eventos reais)
7. Salve e feche

### Teste 2: Motor + Simulador (o teste PODEROSO)

1. Ative os players como acima
2. Clique em **"▶ Iniciar motor"**
3. Depois clique em **"🧪 Iniciar simulador de teste"**
4. **BOOM!** — Log começa a rodar!

O simulador vai gerar:
- Agressões normais dos players (Goldman, JPM, Morgan, Merrill, etc.)
- Ordens no book
- **Rajadas** de 5-15 agressões seguidas

Você vai ver no log:
```
[14:23:15.234] 🎙️ Goldman tomou quinhentos
[14:23:15.891] 🎙️ JPM bateu duzentos
[14:23:16.102] 🎙️ Morgan compra mil no dois
[14:23:17.445] 🔥 [RAJADA INÍCIO] JPM venda · 8 agressões
[14:23:17.812] 🎙️ JPM vendendo
[14:23:20.923] ⏹️ [RAJADA PAROU] JPM venda · vol total 340
[14:23:20.938] 🎙️ JPM parou de vender
```

**Se você configurou NAudio + gravou os WAVs:** o sistema também **TOCA** cada evento!

### Teste 3: Modo LOG puro (sem áudio)

Este é o modo default até você gravar os áudios.

No console de debug do Visual Studio (View → Output → Debug) você vê:
```
🔊 [WOULD PLAY] gs_tomou.wav + 500.wav
🔊 [WOULD PLAY] jpm_bateu.wav + 200.wav
🔊 [WOULD PLAY] JPM parou de vender
```

Isso confirma que **o motor está detectando corretamente**, só falta os áudios!

## 📊 O que os 3 cards de estatística mostram

- **EVENTOS PROCESSADOS**: total de trades/book recebidos
- **EVENTOS NARRADOS**: quantos passaram nos filtros e viraram áudio
- **RAJADAS DETECTADAS**: quantas sequências rápidas foram identificadas

## 🎯 Ajustando sensibilidade

Se o simulador estiver muito silencioso ou muito barulhento:

### Muito silencioso (poucos eventos passando)
- Diminua os limites dos players (compra_mínima, venda_mínima)
- Ative mais players
- Diminua a `SequenciaMinima` da rajada pra 2

### Muito barulhento (todo evento passa)
- Aumente os limites (Goldman=200, JPM=200)
- Diminua o volume master
- Desative players menos importantes

## 🔗 Integração no MarketCore real (depois de validado)

Quando o teste com simulador confirmar que tudo funciona, o Cowork (ou você mesmo) vai fazer a integração real assim:

**No código do MarketCore onde chegam os trades do ProfitDLL**, adicionar:

```csharp
// Se a janela do Pregão Viva Voz estiver aberta
if (_pregaoVivaVozWindow?.IsVisible == true)
{
    _pregaoVivaVozWindow.ViewModel.Engine?.ProcessarAgressao(
        nomeCorretora,   // ex: "Goldman"
        lado,            // "compra" ou "venda"  
        quantidade       // ex: 500
    );
}
```

**Isso é literalmente 3-5 linhas de código.** O motor faz todo o resto (filtrar, montar frase, tocar/logar).

## 🐛 Solução de problemas

**"Motor não inicia"**
- Verifique se o JSON players_catalogo.json está sendo copiado pra bin/
- Log mostra "0 players configurados"? JSON não carregou
- Reveja o Passo 3 do ZIP 1 (Copy to Output Directory)

**"Simulador roda mas nada aparece no log"**
- Confira se você ATIVOU os players (pastilhas verdes)
- Confira se os limites do bloco Agressão não estão muito altos
- Salve as configurações antes de iniciar

**"NAudio não instala"**
- Sem problemas! O sistema roda em modo LOG por padrão
- Você verá os eventos no console do Visual Studio
- Instale NAudio depois quando gravar os áudios

**"Log só mostra 🔊 [WOULD PLAY] em vez de tocar"**
- Isto é o MODO NORMAL sem áudios gravados
- Significa: motor detectou perfeitamente, só falta o WAV
- Ativa quando você gravar áudios no Studio (ZIP 3)

## 🎯 Próximo: ZIP 3

Após validar que:
- ✅ Motor detecta eventos
- ✅ Simulador gera rajadas
- ✅ Log mostra as frases corretas ("Goldman tomou quinhentos", etc.)

Vamos pro **ZIP 3 - Studio de Gravação** onde você grava a sua voz pra cada frase e o sistema começa a **CANTAR de verdade**! 🎙️

---

**Anderson, se você chegou até aqui e o simulador está gerando eventos:**

**PARABÉNS!** 🎉

Você tem um **detector de order flow institucional em tempo real** funcionando no seu MarketCore. Nenhuma plataforma da B3 tem isso.
