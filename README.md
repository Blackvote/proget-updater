В разработке.

Полностью переписанный Updater, теперь на Go

# Для версии прогета 22.0.19  
## Алгоритм работы:
### Получение пакетов:
1. Читает конфиг
2. Из конфига получает 2 набора данных для структуры Proget
```
type Config struct {
	Source      ProgetConfig `yaml:"source"`
	Destination ProgetConfig `yaml:"destination"`
}

type ProgetConfig struct {
	URL    string `yaml:"url"`
	APIKey string `yaml:"api_key"`
	Feed   string `yaml:"feed"`
}
```
3. GET запрос на ```URL/upack/FEED/packages```
4. Из списка пакетов создаются массивы пакетов
5. Сравнивается 2 массива пакетов, в 2 стороны, вначале сравнивается Source->Dest, потом Dest->Source  
5.1 Если пакет есть в Source и его нет в Destination, он будет скачан в ```./packages``` (-p)  
5.1.1 После скачивания пакета он будет отправлен в ```URL/upack/FEED/upload```  
5.2 Если пакет присутствует в Dest, но отсутствует в Source он будет удалён, POST запросом в ```URL/upack/FEED/delete/Group/Name/version```    