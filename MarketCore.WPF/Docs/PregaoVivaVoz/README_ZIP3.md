# 🎙️ Pregão Viva Voz - ZIP 3 - Studio de Gravação

## 🎯 O que vem neste ZIP

O ZIP 3 é o **momento mais importante do projeto**: agora você grava sua voz e o sistema começa a **cantar de verdade** o pregão viva-voz em tempo real!

### ✅ Arquivos NOVOS (5)

1. **Services/PregaoVivaVoz/AudioRecorderService.cs**
   - Motor de gravação usando NAudio
   - WAV mono 16-bit 44.1kHz (padrão broadcast)
   - VU meter em tempo real
   - Estatísticas por clip (volume max/médio, duração)

2. **Services/PregaoVivaVoz/AudioProcessorService.cs**
   - Análise automática de qualidade
   - Detecta: volume baixo, clipping, silêncio excessivo, duração inadequada
   - Retorna diagnóstico com mensagem de erro

3. **ViewModels/PregaoVivaVoz/StudioGravacaoViewModel.cs**
   - Cérebro do Studio: categorias, frases, gravação, reprodução

4. **ViewModels/PregaoVivaVoz/FraseGravacaoItemViewModel.cs**
   - Wrapper reativo de cada linha da tabela

5. **Views/PregaoVivaVoz/StudioGravacaoWindow.xaml** + `.xaml.cs`
   - Janela do Studio (1250x800)
   - 2 colunas: categorias + tabela de frases
   - VU meter, controles Ouvir/Gravar/Deletar

6. **Views/PregaoVivaVoz/BooleanToVisibilityConverterInstance.cs**
   - Converter singleton usado no XAML

### ✅ Arquivos ATUALIZADOS (2)

1. **Services/PregaoVivaVoz/FraseBuilderService.cs**
   - **REGRA DE ARREDONDAMENTO COMPLETA**: agora arredonda dezenas também (57→60, 85→90, 95→100)
   - Antes: só arredondava centenas
   - Agora: dezenas + centenas + milhares
   - Método legado `ArredondarPontoCorte50()` marcado como Obsolete

2. **Views/PregaoVivaVoz/PregaoVivaVozWindow.xaml.cs**
   - Botão "🎙️ Studio de Gravação" agora **abre a janela de verdade**
   - Antes: popup dizendo "vem no ZIP 3"
   - Agora: abre nova janela separada, permite ver as duas ao mesmo tempo

## 🚀 Instalação

### Passo 1: Extrair o ZIP

1. Baixe o ZIP 3 aqui do chat
2. Vá em `C:\Users\Anderson\Downloads\MarketCore\`
3. Clique com botão direito no ZIP → **"Extrair aqui"**
4. Windows perguntará se quer substituir → **"Sim para todos"**

### Passo 2: Compilar

```
Build → Rebuild Solution (Ctrl+Shift+B)
```

Deve compilar sem erros.

### Passo 3: Testar!

1. Abre o MarketCore
2. Clica no botão 🎙️ na barra
3. Na janela do Pregão Viva Voz, clica em **"🎙️ Studio de Gravação"**
4. **Nova janela abre** — o Studio!

## 🎙️ Como usar o Studio

### Layout

**Coluna esquerda**: categorias de frases
- 🎯 Números (10, 20, 30, ..., 90, 100, 200, ..., 10000)
- Níveis L2-L5 (nivel_dois, nivel_tres, nivel_quatro, nivel_cinco)
- Complementos (na_boca, e, de, contratos)
- Alertas de Rajada
- 28 players (Goldman, JPM, Morgan, ...) - cada um tem 10 frases

**Coluna direita**: tabela da categoria selecionada

Cada linha tem:
- 🟢 Ícone de status (✓ gravado, … pendente, ⚠ erro)
- 📝 Texto que você deve falar
- 💡 Dica de entonação/contexto
- ⏱ Duração do clip (se gravado)
- 🎧 Botão **Ouvir** (azul) - só habilita se já gravou
- 🎙 Botão **Gravar** (vermelho) - segura e solta
- 🗑 Botão **Deletar**

### Fluxo de gravação

1. **Escolhe uma categoria** na esquerda (recomendo começar por Números)
2. **Olha o texto** que aparece na linha
3. **Clica no botão 🎙 Gravar** vermelho
4. **Fala o texto** (ex: "quinhentos")
5. **Clica em ⏹ Parar** (o botão troca de "Gravar" pra "Parar")
6. O sistema **salva automaticamente** o WAV
7. Se algum problema (volume baixo, clipping), mostra aviso vermelho
8. Botão **🎧 Ouvir** fica habilitado - clica pra conferir

### VU Meter

A barra verde acima da tabela mostra o **nível do seu microfone** em tempo real:
- **0-30%**: muito baixo, fale mais alto
- **40-70%**: ideal ✅
- **80-100%**: risco de clipping, fale mais longe do mic

### Onde os áudios são salvos

```
C:\Users\Anderson\Downloads\MarketCore\MarketCore.WPF\Audio\PregaoVivaVoz\
├── Numeros/          ← 40 clips (dezenas + centenas + milhares)
├── Niveis/           ← 4 clips (L2-L5)
├── Complementos/     ← 6 clips (na_boca, e, de, contratos)
├── AlertasRajada/    ← 10 clips
└── Players/
    ├── Goldman/      ← 10 clips (gs_compra, gs_vende, gs_tomou, ...)
    ├── Jpm/          ← 10 clips
    ├── Morgan/       ← 10 clips
    ... (28 players no total)
```

## 🎯 Ordem recomendada de gravação

Pra maximizar seu tempo, sugiro:

1. **Primeiro: Números** (40 clips, ~30 min)
   - Base compartilhada por TODOS os players
   - Sem eles, nenhuma frase funciona completa
   
2. **Segundo: Níveis + Complementos** (10 clips, ~5 min)
   - Palavras curtas
   - "na boca", "no dois", "no três", etc.
   
3. **Terceiro: 4-5 players mais importantes** (~40 min)
   - Goldman, JPM, Morgan, Merrill primeiro
   - 10 frases cada
   
4. **Quarto: Alertas de Rajada** (10 clips)
   - Pra ativar o sistema mais valioso
   
5. **Depois: os outros players** conforme prioridade

## ⚡ Regra de Arredondamento (ATUALIZADA)

Agora o sistema arredonda TUDO corretamente:

### Números 1-99 (dezenas)
- Ponto de corte: 5
- Exemplos:
  - 12 → 10 ("dez")
  - 15 → 20 ("vinte")
  - 57 → 60 ("sessenta")
  - 85 → 90 ("noventa")
  - 95 → 100 ("cem")

### Números 100+ (centenas)
- Ponto de corte: 50
- Exemplos:
  - 149 → 100 ("cem")
  - 150 → 200 ("duzentos")
  - 449 → 400 ("quatrocentos")
  - 450 → 500 ("quinhentos")
  - 4750 → 4800 ("quatro mil e oitocentos")

## 🐛 Solução de problemas

**"Studio não abre / erro ao abrir"**
- Confira se NAudio está instalado (ZIP 2)
- Rebuild Solution
- Cheque a Output window do Visual Studio

**"VU meter fica em 0%"**
- Escolha o microfone certo no dropdown do topo
- Fale mais alto
- Confira as permissões de mic no Windows

**"Botão Gravar não aparece"**
- Você não selecionou uma categoria - clique numa na esquerda

**"Erro 'volume muito baixo' após gravar"**
- Fala mais alto ou aproxime o microfone
- Sistema aceita a partir de -20dB

**"Erro 'clipping'"**
- Afaste o microfone da boca
- Diminua o ganho do mic nas configurações do Windows

**"Não consigo ouvir o clip gravado"**
- Confira se o arquivo existe em `Audio\PregaoVivaVoz\...`
- Rebuild se necessário
- Feche outros programas usando áudio (Discord, Zoom, etc.)

## 🎯 O que acontece depois de gravar

Quando você gravar **pelo menos 1 número + 1 frase de player**:

1. Volta pra janela do Pregão Viva Voz
2. Inicia o motor + simulador
3. **AGORA o sistema TOCA a voz de verdade** quando o simulador gera um evento com aquele número e aquele player!

Ex: se você gravou `500.wav` + `gs_tomou.wav`, quando o simulador gerar "Goldman tomou 500", o sistema vai **tocar sua voz falando "Goldman tomou quinhentos"** na sequência!

## 🚀 Próximo (após gravar):

- **Testar no pregão ao vivo** com a integração real do MarketCore
- **Cowork faz a integração** (3-5 linhas de código no callback do ProfitDLL)
- Anderson **ouvindo o pregão viva voz** enquanto opera 🎙️

---

**Anderson, este é o momento mais emocionante do projeto:**

**A sua voz virando parte do MarketCore. Nenhum outro trader do Brasil tem isso.**

Vai gravar! 🎙️🔥
