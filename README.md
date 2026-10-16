# Systenclin

Limpeza e otimização do Windows. Construído  — **sem reaproveitar código, assets ou marca**.

## Estado

| Versão | Módulo | Situação |
|---|---|---|
| **v0.1** | Limpeza de arquivos temporários | ✅ pronta |
| **v0.2** | Privacidade dos navegadores | ✅ pronta |
| **v0.3** | Startup manager | ✅ pronta |
| **v0.4** | Memória, DNS, processos e monitor ao vivo | ✅ pronta |
| **v0.5** | Disco e espaço (otimização, duplicados, arquivos grandes, pastas) | ✅ pronta |
| **v0.6** | Aplicativos: atualizar e desinstalar (winget) | ✅ pronta |
| **v0.7** | Ponto de restauração + undo (Lixeira, backups, histórico) | ✅ pronta |
| **v0.8** | Extras: atalhos quebrados, lixeira, barra de status, aceleração em 1 clique, rede conservadora | ✅ pronta |

## Rodar

```bash
cd Systenclin
dotnet run -c Release
```

Executável pronto: `dist/Systenclin.exe` (≈ 775 KB + `e_sqlite3.dll`),
arquivo único, exige o .NET Desktop Runtime 10.

## Testes

```bash
cd Testes/Systenclin.Testes
dotnet run -c Release
```

**62 verificações, 0 falhas.** O foco é o que pode dar errado de forma
perigosa, não cobertura bonita. O harness compila `Core/*.cs` direto — sem
UI — e usa o framework de desktop só pelo EventLog dos eventos de boot.

---

## v0.1 — Arquivos temporários

**Análise** — só lê. Mede 16 alvos conhecidos: TEMP, cache do Windows Update,
miniaturas, relatórios de erro, cache de NuGet/npm/pip, logs antigos.

**Limpeza** — arquivos vão para a **Lixeira** (recuperáveis). Pastas de cache
são removidas de vez, e a UI diz isso em vez de prometer undo que não existe.

### Regras de segurança (cobertas por teste)

1. **Nunca atravessa junção nem link simbólico.** Um junction em `%TEMP%`
   apontando para `C:\Windows` faria o limpador apagar o Windows.
2. **9 áreas proibidas**, checadas na varredura **e de novo** na hora de apagar.
3. **Recusa raiz de unidade, caminhos rasos e travessia** (`..`).
4. **Risco alto nunca vem marcado.** A Lixeira também não.
5. **Uma falha não interrompe o resto.** O motivo fica registrado.

---

## v0.2 — Privacidade dos navegadores

Lê e limpa histórico, cookies, formulários, downloads, permissões, cache e
abas salvas de **Chrome, Edge, Firefox, Brave, Opera e Vivaldi** — todos os
perfis de cada um (nesta máquina: 8 perfis).

### O que muda em relação ao v0.1

O v0.1 media bytes. Aqui **não basta medir** — o usuário precisa ver o que vai
apagar. Cada item mostra uma prévia legível:

```
Histórico de navegação — Google Chrome — Padrão
  15/10/26 22:14  ×3   Documentação .NET
      https://learn.microsoft.com/dotnet/...
  15/10/26 21:58  ×1   GitHub — systenclin
      https://github.com/...
```

### As três exigências que não se negociam

**1. Navegador fechado para dados de banco.**

Não existe forma segura de escrever no SQLite de um navegador aberto: ele
mantém o estado em memória e sobrescreve, ou corrompe o banco. O app detecta
e **recusa**, dizendo qual navegador fechar. Produtos que prometem "limpar com
o navegador aberto" ou mentem, ou corrompem dados.

Cache e abas salvas são arquivos em disco — esses podem ser limpos agora.

**2. Backup antes de apagar.**

Cada banco é copiado para `%LOCALAPPDATA%\Systenclin\backup-privacidade\`
antes de qualquer escrita. É a única forma honesta de oferecer desfazer —
o v0.1 não conseguia, este consegue. O botão **Backups** lista e explica como
restaurar.

**3. Categoria inteira, nunca cookie avulso.**

Meia-medida deixa o usuário achando que limpou e não limpou.

### Leitura com o navegador aberto

O app lê mesmo com Chrome e Edge rodando, abrindo o arquivo com
`FILE_SHARE_READ | FILE_SHARE_WRITE`. Um `File.ReadAllBytes` comum falharia
com "arquivo em uso".

### Riscos, ditos com clareza

| Dado | Risco | Marcado por padrão? |
|---|---|---|
| Histórico de navegação | baixo | sim |
| Cache do navegador | baixo | sim |
| Histórico de downloads | baixo | sim |
| Formulários preenchidos | médio | **não** |
| Abas salvas | médio | **não** |
| Permissões de site | médio | **não** |
| **Cookies** | **alto** | **não** |

Cookies desconectam de todas as contas. O aviso aparece no diálogo de
confirmação, em texto, não escondido num tooltip.

---

## v0.3 — Startup manager

Ver, desabilitar e reabilitar o que sobe com o Windows, nas cinco fontes:
chave `Run` do usuário, chave `Run` da máquina (visões 64 e 32 bits) e as duas
pastas Inicializar.

**Desativar não perde nada.** O valor original — nome, tipo e dados — é
gravado em `%LOCALAPPDATA%\Systenclin\inicializacao.json` **antes** de sair do
registro (e o store é desfeito se a escrita falhar). Arquivos da pasta
Inicializar são movidos para `inicializacao_desativados`. Ativar desfaz pelo
mesmo caminho guardado. Itens de máquina exigem administrador, e a UI diz
isso em vez de falhar em silêncio.

## v0.4 — Memória, DNS, processos e monitor

- **Liberar memória** — poda o working set dos processos que passam de 20 MB.
- **Limpar cache DNS** — flush direto.
- **Processos** — top 15 por memória; encerrar confirma em dois cliques.
- **Monitor ao vivo** — CPU e RAM com histórico de 60 s, na aba Otimização e
  no rodapé a partir do v0.8. `GetSystemTimes` + `GlobalMemoryStatusEx`, sem
  `PerformanceCounter`. Só roda com a janela aberta — nada residente.

## v0.5 — Disco e espaço

- **Otimizar disco** — detecta SSD ou HDD (penalidade de busca do volume) e
  roda `defrag /O`: TRIM em SSD, desfragmentação em HDD. Exige administrador.
- **Duplicados** — agrupa por hash; os marcados vão para a Lixeira.
- **Arquivos grandes** — ordenados por tamanho; Lixeira.
- **Análise de pastas** — o que ocupa espaço, pasta por pasta.

## v0.6 — Aplicativos (winget)

Atualizar (individual ou todos) e desinstalar via winget. Sem winget, a UI
explica em vez de estourar erro. A desinstalação usa o fluxo do próprio
winget — nada silencioso.

## v0.7 — Restauração e undo

- **Ponto de restauração** — opcional, antes de limpar, quando aberto como
  administrador (o checkbox fica na barra de confirmação).
- **Undo em camadas** — arquivos vão para a Lixeira; dados de navegador têm
  backup em `%LOCALAPPDATA%\Systenclin\backup-privacidade\`; toda operação
  fica no **Histórico** (`CleanLog`, JSON).

## v0.8 — Extras

- **Atalhos quebrados** — `.lnk` apontando para alvo inexistente, no Desktop
  e no Menu Iniciar; os marcados vão para a Lixeira.
- **Lixeira** — conta itens e tamanho (`SHQueryRecycleBin`) e esvazia com
  confirmação em dois cliques (`SHEmptyRecycleBin`).
- **Barra de status** — CPU e RAM no rodapé, ao lado de Backups e Histórico.
- **Aceleração em 1 clique** — devolve memória ociosa e limpa o cache DNS
  numa ação, com resultado consolidado; o botão se desabilita enquanto outra
  operação da aba Otimização roda.
- **Rede** — lista os adaptadores do Windows (tipo, estado, velocidade e
  IPv4); renovar o endereço IP (`ipconfig /renew`) e limpar o cache ARP
  (`netsh`) exigem administrador. Conservador de propósito: sem tweak de
  TCP/registro e sem reset de winsock.

---

## O que este app NÃO faz (de propósito)

- **Sem driver de kernel.** O ASC instala dois minifiltros que interceptam
  arquivo e registro. Aqui não tem nada disso.
- **Sem serviço residente.** Não fica rodando de fundo.
- **Sem instalação silenciosa.** Nada de serviço `Auto` sem aviso.
- **Sem limpador de registro.** Risco desproporcional ao ganho; ficou de fora.
- **Sem venda cruzada.** Não oferece outros produtos.

---

## Estrutura

```
Systenclin/
├── Core/
│   ├── — limpeza (v0.1)
│   ├── ScanCategory.cs        categorias de arquivo, risco, explicação
│   ├── CleanableItem.cs       um item de arquivo
│   ├── ScanTargets.cs         alvos + guarda de segurança
│   ├── DirectoryMeasurer.cs   mede árvore sem seguir links
│   ├── Scanner.cs             varredura de arquivos
│   ├── Cleaner.cs             limpeza + envio à Lixeira
│   ├── FileDeleter.cs         remoção definitiva de pastas de cache
│   ├── CleanLog.cs            histórico em JSON
│   ├── — privacidade (v0.2)
│   ├── BrowserProfile.cs      descoberta de perfis de navegador
│   ├── PrivacyItem.cs         um item de privacidade, com prévia
│   ├── PrivacyScanner.cs      leitura dos bancos (SQLite)
│   ├── PrivacyCleaner.cs      limpeza com backup e restauração
│   ├── SharedFileReader.cs    leitura de arquivo travado
│   ├── — otimização e ferramentas (v0.3+)
│   ├── StartupManager.cs      o que sobe com o Windows
│   ├── MemoryOptimizer.cs     poda de working set
│   ├── DnsFlusher.cs          limpa o cache DNS
│   ├── NetworkInfo.cs         adaptadores e ações de rede
│   ├── BootTimeReader.cs      tempo de boot via eventos do Windows
│   ├── ProcessManager.cs      processos por memória + encerrar
│   ├── SystemMonitor.cs       CPU/RAM ao vivo
│   ├── RecycleBin.cs          consultar e esvaziar a Lixeira
│   ├── DiskOptimizer.cs       TRIM/desfragmentação com detecção de mídia
│   ├── RestorePoint.cs        ponto de restauração
│   ├── DuplicateFinder.cs     duplicados por hash
│   ├── LargeFileFinder.cs     arquivos grandes
│   ├── FolderAnalyzer.cs      o que ocupa espaço, pasta por pasta
│   ├── ShortcutFixer.cs       atalhos quebrados
│   ├── AppUpdater.cs          atualizações via winget
│   └── AppUninstaller.cs      desinstalação via winget
├── MainWindow.xaml            interface com oito abas
└── app.manifest               asInvoker (sem admin por padrão)

Testes/Systenclin.Testes/      as 62 verificações (compila o Core direto)
dist/                          Systenclin.exe publicado
```

## Próximo passo

**Próximo:** corrigir a lista de atualizações da aba Aplicativos — o winget
encontra 27 atualizações no terminal, mas o app não as mostra. Demais
incrementos conforme prioridade.
