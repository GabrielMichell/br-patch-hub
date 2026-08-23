# TradutorHub

Base de um aplicativo desktop para distribuir, instalar e atualizar traduções de jogos a partir de um catálogo hospedado no GitHub.

## O que esta base já faz

- Lê um catalog.json público hospedado no GitHub.
- Lista várias traduções em um catálogo modular.
- Baixa cada tradução como um ZIP a partir de uma release do GitHub.
- Valida o SHA-256 do pacote quando o campo sha256 é informado.
- Detecta jogos em pastas comuns da Steam quando executableHints é informado.
- Permite escolher manualmente a pasta do jogo.
- Instala somente os arquivos declarados em install.
- Mantém registro dos arquivos instalados e dos backups originais.
- Impede que uma tradução sobrescreva arquivos registrados como pertencentes a outra tradução.
- Atualiza uma tradução removendo a versão anterior de forma controlada.
- Desinstala e restaura arquivos originais quando possível.
- Atualiza o catálogo ao abrir e depois a cada 6 horas.

## Como executar

1. Dê duplo clique em TradutorHub.bat, ou abra o PowerShell.
2. Para executar pelo PowerShell:

~~~powershell
powershell -ExecutionPolicy Bypass -File .\TradutorHub.ps1
~~~

O Windows pode mostrar um aviso porque este protótipo ainda não possui assinatura digital. Na versão distribuível, o próximo passo é empacotar e assinar o aplicativo.

## Como publicar as traduções no GitHub

Você pode ter um repositório só para o catálogo e um repositório por tradução, ou colocar tudo em um repositório central. O aplicativo só precisa de uma URL pública para o catalog.json.

Exemplo de estrutura de um repositório de tradução:

~~~text
catalog.json
releases/
  meu-jogo-ptbr.zip
~~~

O ZIP deve conter a pasta indicada em install.from. Por exemplo:

~~~text
meu-jogo-ptbr.zip
└── files/
    ├── localization/
    │   └── pt-BR.lng
    └── readme.txt
~~~

No catalog.json, a regra:

~~~json
{
  "from": "files",
  "to": "game"
}
~~~

copia o conteúdo de files para a pasta escolhida do jogo. Também é possível usar uma subpasta:

~~~json
{
  "from": "files/localization",
  "to": "localization"
}
~~~

O arquivo catalog.example.json nesta pasta mostra todos os campos disponíveis.

## URL do catálogo

Na primeira execução, substitua:

~~~text
https://raw.githubusercontent.com/SEU_USUARIO/tradutor-hub-catalogo/main/catalog.json
~~~

pela URL raw do seu repositório, por exemplo:

~~~text
https://raw.githubusercontent.com/seu-usuario/tradutor-hub-catalogo/main/catalog.json
~~~

O catálogo pode ser atualizado sem recompilar o aplicativo.

## Arquivos locais de dados

O aplicativo salva dados em:

~~~text
%LOCALAPPDATA%\TradutorHub
~~~

Ali ficam o catálogo em cache, as configurações, o registro de instalações e os backups. Os backups são separados por tradução e versão.

## Limitações intencionais desta primeira base

Esta versão trabalha com cópia de arquivos declarada no manifesto. Alguns jogos exigem operações específicas, como editar arquivos Unity, alterar manifestos, aplicar patch binário ou instalar por um gerenciador de mods. A arquitetura já separa o catálogo do instalador, então esses casos podem receber adaptadores modulares em uma próxima etapa sem alterar a tela principal.

O aplicativo não possui um GitHub próprio. Ele foi preparado para consumir o seu repositório público; quando você me passar o usuário/repositório, a URL padrão pode ser ajustada.
