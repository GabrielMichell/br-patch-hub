# BR Patch Hub

Aplicativo desktop para pesquisar traduções e instalá-las diretamente em jogos da Steam, sem abrir outro instalador.

Versão oficial atual: **v3.1.1**.

## O que esta base faz

- Lê automaticamente o `catalog.json` público hospedado no GitHub.
- Permite pesquisar o nome do jogo no topo da tela.
- Mostra uma mensagem clara quando ainda não existe tradução cadastrada.
- Detecta bibliotecas Steam padrão e bibliotecas adicionais do `libraryfolders.vdf`.
- Baixa somente pacotes ZIP declarados no catálogo.
- Suporta uma tradução com vários ZIPs, como o Lucius II.
- Valida o SHA-256 antes de extrair qualquer arquivo.
- Bloqueia caminhos perigosos dentro dos ZIPs.
- Faz backup dos arquivos originais antes de substituir.
- Detecta conflito com arquivos pertencentes a outra tradução.
- Permite atualizar e remover a tradução pelo próprio app.
- Compara semanticamente a versão instalada com a versão mais recente do catálogo.
- Preserva e restaura automaticamente a tradução anterior caso uma atualização falhe.
- Atualiza o catálogo automaticamente ao abrir.
- Verifica atualizações do próprio BR Patch Hub, baixa o novo executável, valida o SHA-256 e reinicia o aplicativo.
- Exibe biblioteca visual com capas, filtros, status e resumo das traduções.
- Mostra o progresso de download, instalação, remoção e verificação sem congelar a interface.
- Trata operações especiais de pacote, como anexar um payload a um arquivo existente.

O BR Patch Hub não executa arquivos `.exe`. O formato recomendado para cada projeto é um ou mais ZIPs com os arquivos da tradução e as regras de instalação no catálogo.

## Como executar

1. Dê duplo clique em `BR Patch Hub.exe`.
2. O aplicativo já aponta para:

~~~text
https://raw.githubusercontent.com/GabrielMichell/br-patch-hub/main/catalog.json
~~~

3. Digite o nome do jogo no campo de busca.
4. Selecione a tradução encontrada.
5. Use `Escanear biblioteca` para localizar os jogos da Steam.
6. Selecione o jogo e clique em `Instalar tradução`.

## Como cadastrar uma tradução ZIP

Cada item do `catalog.json` usa `packageType` `zip` ou `multi-zip`, uma lista de `assets` e regras `install`.
O campo opcional `changelog` aceita uma lista de alterações da versão atual e é exibido quando houver atualização disponível.

~~~json
{
  "id": "meu-jogo-ptbr",
  "game": "Nome do jogo",
  "language": "pt-BR",
  "version": "1.0.0",
  "changelog": ["Correções de textos", "Ajustes de compatibilidade"],
  "packageType": "zip",
  "folderHints": ["Nome da pasta Steam"],
  "install": [
    { "from": "MeuJogo_Data", "to": "game" }
  ],
  "assets": [
    {
      "role": "package",
      "fileName": "Minha-Traducao-v1.0.zip",
      "downloadUrl": "https://github.com/usuario/repositorio/releases/download/v1.0.0/Minha-Traducao-v1.0.zip",
      "sha256": "HASH_SHA256_DO_ZIP"
    }
  ]
}
~~~

`from` é o caminho dentro do ZIP. `to: game` significa a pasta principal detectada do jogo. O app também encontra a pasta declarada mesmo que o ZIP tenha uma pasta externa com o nome da release.

Para várias partes, use vários assets com `role: package` e uma regra que copie o conteúdo extraído:

~~~json
{
  "packageType": "multi-zip",
  "install": [
    { "from": ".", "to": "game" }
  ],
  "assets": [
    {
      "role": "package",
      "fileName": "Dados-01.zip",
      "downloadUrl": "https://github.com/usuario/repositorio/releases/download/v1.0.0/Dados-01.zip",
      "sha256": "HASH_DA_PARTE_01"
    },
    {
      "role": "package",
      "fileName": "Dados-02.zip",
      "downloadUrl": "https://github.com/usuario/repositorio/releases/download/v1.0.0/Dados-02.zip",
      "sha256": "HASH_DA_PARTE_02"
    }
  ]
}
~~~

Para jogos que exigem uma operação especial, o catálogo aceita `operations` com `copy` ou `append`. O `append` pode declarar `alignment` e `expectedSize` para impedir que um patch seja aplicado sobre uma versão errada do arquivo.

## Arquivos locais

Configurações, cache, pacotes baixados e backups ficam em:

~~~text
%LOCALAPPDATA%\BR Patch Hub
~~~

O catálogo completo e um modelo editável estão em `catalog.json` e `catalog.example.json`.

## Segurança

- O app aceita somente URLs HTTPS do GitHub.
- Cada pacote deve declarar SHA-256 para validação de integridade.
- Os ZIPs são validados contra path traversal antes da extração.
- O app não executa EXE, DLL, script ou outro programa vindo da release.
- O backup é criado dentro da área local do BR Patch Hub antes da instalação.

## Créditos

BR Patch Hub criado por Gabriel Michel e Fl4sh9174.

- É necessário possuir uma cópia legítima do jogo e fechar o jogo antes de instalar.

