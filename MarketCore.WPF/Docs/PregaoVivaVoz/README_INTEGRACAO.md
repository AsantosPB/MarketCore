# 🎙️ Pregão Viva Voz - Instalação e Integração

## 📦 Sobre este ZIP

**Este é o ZIP 1 de 3** — traz a base arquitetural + janela principal navegável.

- ✅ Models completos (PlayerConfig, FiltroBook, FiltroAgressao, FiltroRajada, EventoOrderFlow, FraseGravacao, BufferRajada)
- ✅ Catálogo JSON das 28 corretoras pré-cadastradas
- ✅ Config global de rajada persistida em JSON
- ✅ 345 frases a gravar já listadas no JSON
- ✅ Serviço de persistência (ConfigPersistenceService)
- ✅ ViewModel principal com bindings completos
- ✅ Janela XAML com os 4 blocos (Players + Book + Agressão + Rajada)
- ✅ Scroll interno em cada bloco, busca funcionando
- ✅ Salvar configurações → JSON automaticamente

**O que ainda NÃO vem nesta fase:**
- ❌ Motor de detecção em tempo real → **ZIP 2**
- ❌ Reprodução de áudio (NAudio) → **ZIP 2**
- ❌ Studio de Gravação → **ZIP 3**
- ❌ Conexão com ProfitDLL → **ZIP 2**

---

## 🚀 Instalação (5 minutos)

### Passo 1: Extrair o ZIP

1. Baixe o ZIP aqui do chat
2. Vá em `C:\Users\Anderson\Downloads\MarketCore\`
3. Clique com botão direito no ZIP → **"Extrair aqui"**
4. Windows perguntará se quer **substituir/mesclar** arquivos → clique **"Sim para todos"**

Os arquivos vão se organizar dentro da estrutura existente do MarketCore, sem quebrar nada.

### Passo 2: Adicionar arquivo ao projeto no Visual Studio

1. Abra `MarketCore.sln` no Visual Studio
2. Na **Solution Explorer**, clique com botão direito no projeto **MarketCore.WPF**
3. Escolha **"Adicionar → Item Existente"** (Add → Existing Item)
4. Navegue até as pastas novas e adicione TODOS os arquivos:
   - `Models\PregaoVivaVoz\*.cs` (7 arquivos)
   - `ViewModels\PregaoVivaVoz\*.cs` (4 arquivos)
   - `Services\PregaoVivaVoz\*.cs` (1 arquivo)
   - `Views\PregaoVivaVoz\*.xaml` e `.xaml.cs` (2 arquivos)

**Alternativa mais fácil:** Se o projeto usa **SDK-style** (`.csproj` moderno), os arquivos são **incluídos automaticamente**. Só recarregar o projeto (**Build → Rebuild Solution**).

### Passo 3: Copiar os JSON de config

Os arquivos JSON precisam ser **copiados pra pasta de saída**. Existem 2 formas:

**Opção A (recomendada):** editar `MarketCore.WPF.csproj` e adicionar:

```xml
<ItemGroup>
  <None Update="Config\PregaoVivaVoz\*.json">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

**Opção B (rápida):** clicar em cada .json na Solution Explorer, ir em **Properties (F4)** e configurar:
- **Build Action:** `Content`
- **Copy to Output Directory:** `Copy if newer`

Isso garante que os JSON sejam copiados pra pasta `bin/Debug/net9.0-windows/Config/PregaoVivaVoz/` quando o projeto compilar.

### Passo 4: Adicionar botão no menu do MainWindow

Localize seu arquivo `MainWindow.xaml` (janela principal do MarketCore) e adicione um botão pra abrir o módulo. Exemplo:

```xml
<Button Content="🎙️ Pregão Viva Voz" 
        Click="AbrirPregaoVivaVoz_Click"
        Margin="5"/>
```

E no `MainWindow.xaml.cs`:

```csharp
using MarketCore.WPF.Views.PregaoVivaVoz;

private void AbrirPregaoVivaVoz_Click(object sender, RoutedEventArgs e)
{
    var janela = new PregaoVivaVozWindow();
    janela.Owner = this;
    janela.Show();
}
```

### Passo 5: Compilar e testar

1. **Build → Rebuild Solution** (Ctrl+Shift+B)
2. **F5** pra rodar
3. Clique no botão **"🎙️ Pregão Viva Voz"** no MarketCore
4. A janela abre com as 28 corretoras carregadas

---

## ✅ Como validar que funcionou

Ao abrir a janela, você deve ver:

- ✅ **Bloco verde no topo:** grid com pastilhas das 28 corretoras (Goldman, JPM, Morgan, etc.)
- ✅ **Bloco azul:** tabela de filtros do BOOK, todas 28 corretoras com valores editáveis
- ✅ **Bloco laranja:** tabela de filtros de AGRESSÃO, todas 28 corretoras
- ✅ **Bloco roxo:** parâmetros da rajada + lista de participantes

**Teste rápido:**
1. Clique em algumas pastilhas (Goldman, JPM, Merrill) → devem ficar verdes
2. Digite "Gold" no campo busca do bloco Book → filtra
3. Altere o valor de compra do Goldman pra 500 → aceita
4. Clique **💾 Salvar** → mensagem no rodapé confirma
5. Feche a janela e reabra → os valores devem persistir

---

## 📁 Estrutura de arquivos criada

```
MarketCore.WPF/
├── Models/PregaoVivaVoz/
│   ├── PlayerConfig.cs
│   ├── FiltroBook.cs
│   ├── FiltroAgressao.cs
│   ├── FiltroRajada.cs
│   ├── EventoOrderFlow.cs
│   ├── FraseGravacao.cs
│   └── BufferRajada.cs
│
├── ViewModels/PregaoVivaVoz/
│   ├── ViewModelBase.cs
│   ├── RelayCommand.cs
│   └── PregaoVivaVozViewModel.cs
│
├── Services/PregaoVivaVoz/
│   └── ConfigPersistenceService.cs
│
├── Views/PregaoVivaVoz/
│   ├── PregaoVivaVozWindow.xaml
│   └── PregaoVivaVozWindow.xaml.cs
│
├── Config/PregaoVivaVoz/
│   ├── players_catalogo.json      ← 28 corretoras
│   ├── config_rajada_global.json  ← params da rajada
│   └── frases_a_gravar.json       ← 345 frases pré-listadas
│
└── Audio/PregaoVivaVoz/
    ├── Players/       ← 28 pastas vazias
    ├── Numeros/       ← vazio, ZIP 3 usa
    ├── Niveis/        ← vazio, ZIP 3 usa
    ├── Complementos/  ← vazio, ZIP 3 usa
    └── AlertasRajada/ ← vazio, ZIP 3 usa
```

---

## 🎯 Próximos passos

Depois de testar este ZIP e confirmar que a janela abre corretamente, avise que vou entregar o **ZIP 2** contendo:

- 🔧 Motor `PregaoVivaVozEngine` conectado ao `IMarketDataProvider`
- 🎯 `DetectorRajadaService` com precisão de milissegundos
- 🔊 `AudioPlaybackService` com NAudio (fila FIFO + crossfade)
- 🎼 `FraseBuilderService` (monta as frases: "Goldman tomou" + "500")
- 🎧 Auto-detecção de ambiente (ProfitDLL vs Simulator)

O ZIP 3 vai trazer o **Studio de Gravação** completo com botões Ouvir + Gravar em cada linha.

---

## 🐛 Solução de problemas

**Erro:** "Arquivo players_catalogo.json não encontrado"
- **Causa:** o JSON não está sendo copiado pra pasta bin/
- **Solução:** siga o Passo 3 (configurar Copy to Output Directory)

**Erro:** "The name 'PregaoVivaVozWindow' does not exist"
- **Causa:** arquivos não foram adicionados ao projeto
- **Solução:** faça um Rebuild Solution (Ctrl+Shift+B)

**Erro:** "InvalidOperationException: 'IEnumerable' does not contain a definition"
- **Causa:** falta um `using System.Collections.Generic`
- **Solução:** verifique se todos os `.cs` compilaram (o ViewModel principal já tem isso)

**Janela abre mas fica vazia (sem corretoras):**
- **Causa:** o JSON não foi encontrado
- **Solução:** verifique se existe `bin\Debug\net9.0-windows\Config\PregaoVivaVoz\players_catalogo.json` depois de compilar

---

**Feito com ❤️ pra Anderson Santos - MarketCore FlowSense v1.0**
