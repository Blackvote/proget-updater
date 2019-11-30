# Updater

Утилита для выравнивания пакетов между центральным ProGet(BF) и ProGet эксплуатаций

## Getting Started

Запуск должен осуществляться от имени администратора

Редактируем файл конфигурации и запускаем приложение. 

Для синхронизации nuget feeds необходим dotnet sdk 2.2
https://dotnet.microsoft.com/download/dotnet-core/thank-you/sdk-2.2.207-windows-x64-installer

### Prerequisites

Конфигурация приложения
!!!Обратите внимание на обязательное наличие слешей в конце адресов!!!
```
{
  "SourceProget": {
    "Address": "https://proget.netsrv.it:38443/",
    "FeedName": "Neo",
    "ApiKey": "ikvx7RRCAXXLTvJ-uxJw"
  },
  "DestProget": {
    "Address": "https://pg.orpo.netsrv.pw/",
    "FeedName": "Neo",
    "ApiKey": "RezQyJmfcWxxCawMiy1g"
  }
}

```
также есть возможность синхронизировать сразу несколько фидов, пример конфига:
```
[
{
  "SourceProget": {
    "Address": "https://proget.netsrv.it:38443/",
    "FeedName": "sourceFeed",
    "ApiKey": "someApiKey"
  },
  "DestProget": {
    "Address": "https://pg.orpo.netsrv.pw/",
    "FeedName": "destFeed",
    "ApiKey": "someApiKey"
  }
},
{
  "SourceProget": {
    "Address": "https://proget.netsrv.it:38443/",
    "FeedName": "someFeed",
    "ApiKey": "someApiKey"
  },
  "DestProget": {
    "Address": "https://pg.orpo.netsrv.pw/",
    "FeedName": "drugoiFeed",
    "ApiKey": "someApiKey"
  }
}
]

```

Так же для ApiKey необходимо выставить права на "Feed Management API" настройках ProGet(administration/api-keys)