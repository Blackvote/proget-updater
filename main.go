package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"gopkg.in/yaml.v2"
	"io"
	"io/ioutil"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

type Config struct {
	Source      ProgetConfig `yaml:"source"`
	Destination ProgetConfig `yaml:"destination"`
}

type ProgetConfig struct {
	URL    string `yaml:"url"`
	APIKey string `yaml:"api_key"`
	Feed   string `yaml:"feed"`
}

type Package struct {
	Group    string   `json:"group"`
	Name     string   `json:"name"`
	Versions []string `json:"versions"`
}

func main() {
	configFile := flag.String("f", "config.yml", "path to config file")
	savePath := flag.String("p", "./packages", "path to save downloaded packages")
	flag.Parse()

	config, err := readConfig(*configFile)
	if err != nil {
		log.Fatalf("Failed to read config: %v", err)
	}

	sourcePackages, err := getPackages(config.Source)
	if err != nil {
		log.Fatalf("Failed to get packages from source: %v", err)
	}

	destPackages, err := getPackages(config.Destination)
	if err != nil {
		log.Fatalf("Failed to get packages from destination: %v", err)
	}

	err = syncPackages(config, sourcePackages, destPackages, *savePath)
	if err != nil {
		log.Fatalf("Failed to sync packages: %v", err)
	}
}

func readConfig(configFile string) (*Config, error) {
	data, err := ioutil.ReadFile(configFile)
	if err != nil {
		return nil, err
	}

	var config Config
	err = yaml.Unmarshal(data, &config)
	if err != nil {
		return nil, err
	}

	return &config, nil
}

func getPackages(progetConfig ProgetConfig) ([]Package, error) {
	url := fmt.Sprintf("%s/upack/%s/packages", progetConfig.URL, progetConfig.Feed)
	req, err := http.NewRequest("GET", url, nil)
	if err != nil {
		return nil, err
	}

	req.Header.Set("X-ApiKey", progetConfig.APIKey)
	client := &http.Client{}
	resp, err := client.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("failed to get packages: %s", resp.Status)
	}

	var packages []Package
	err = json.NewDecoder(resp.Body).Decode(&packages)
	if err != nil {
		return nil, err
	}

	fmt.Printf("Количество пакетов, найденных в %s/%s: %d\n", progetConfig.URL, progetConfig.Feed, len(packages))

	fmt.Printf("Список пакетов: ")
	for _, pkg := range packages {
		fmt.Printf("%s/%s: %s | ", pkg.Group, pkg.Name, strings.Join(pkg.Versions, " "))
	}
	fmt.Println()

	return packages, nil
}

func syncPackages(config *Config, sourcePackages, destPackages []Package, savePath string) error {
	destPackageMap := make(map[string]map[string]bool)
	for _, pkg := range destPackages {
		key := fmt.Sprintf("%s:%s", pkg.Group, pkg.Name)
		if destPackageMap[key] == nil {
			destPackageMap[key] = make(map[string]bool)
		}
		for _, version := range pkg.Versions {
			destPackageMap[key][version] = true
		}
	}
	sourcePackageMap := make(map[string]map[string]bool)
	for _, pkg := range sourcePackages {
		key := fmt.Sprintf("%s:%s", pkg.Group, pkg.Name)
		if sourcePackageMap[key] == nil {
			sourcePackageMap[key] = make(map[string]bool)
		}
		for _, version := range pkg.Versions {
			sourcePackageMap[key][version] = true
		}
	}

	var wg sync.WaitGroup
	for _, pkg := range sourcePackages {
		for _, version := range pkg.Versions {
			key := fmt.Sprintf("%s:%s", pkg.Group, pkg.Name)
			if !destPackageMap[key][version] {
				fmt.Printf("S-D %s:%s:%s not found. Syncing\n", pkg.Group, pkg.Name, version)
				wg.Add(1)
				go func(pkg Package, version string) {
					defer wg.Done()
					err := downloadAndUploadPackage(config, pkg, version, savePath)
					if err != nil {
						log.Printf("Failed to sync package %s:%s: %v", pkg.Name, version, err)
					}
				}(pkg, version)
			} else {
				fmt.Printf("S-D %s:%s:%s found.\n", pkg.Group, pkg.Name, version)
			}
		}
	}

	for _, pkg := range destPackages {
		for _, version := range pkg.Versions {
			key := fmt.Sprintf("%s:%s", pkg.Group, pkg.Name)
			if !sourcePackageMap[key][version] {
				wg.Add(1)
				go func(pkg Package, version string) {
					defer wg.Done()
					err := deleteFile(config, pkg, version)
					if err != nil {
						log.Printf("Failed to sync package %s:%s: %v\n", pkg.Name, version, err)
					}
				}(pkg, version)
			} else {
				fmt.Printf("D-S %s:%s:%s found.\n", pkg.Group, pkg.Name, version)
			}
		}
	}
	wg.Wait()
	return nil
}

func downloadAndUploadPackage(config *Config, pkg Package, version, savePath string) error {
	downloadURL := fmt.Sprintf("%s/upack/%s/download/%s/%s/%s", config.Source.URL, config.Source.Feed, pkg.Group, pkg.Name, version)
	uploadURL := fmt.Sprintf("%s/upack/%s/upload", config.Destination.URL, config.Destination.Feed)

	packagePath := filepath.Join(savePath)

	os.MkdirAll(packagePath, os.ModePerm)

	filePath := filepath.Join(packagePath, fmt.Sprintf("%s.%s.upack", pkg.Name, version))

	err := downloadFile(downloadURL, config.Source.APIKey, filePath)
	if err != nil {
		return err
	}

	return uploadFile(uploadURL, config.Destination.APIKey, filePath)
}

func downloadFile(url, apiKey, filePath string) error {
	fmt.Printf("Скачивание пакета %s\n", filePath)
	//req, err := http.NewRequest("GET", url, nil)
	//if err != nil {
	//	return err
	//}

	// Создаем контекст с тайм-аутом
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()

	req, err := http.NewRequestWithContext(ctx, "GET", url, nil)
	if err != nil {
		return err
	}

	req.Header.Set("X-ApiKey", apiKey)

	client := &http.Client{}
	resp, err := client.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		bodyBytes, err := ioutil.ReadAll(resp.Body)
		if err != nil {
			return fmt.Errorf("cant read body from request. Failed to download file: %s", resp.Status)
		}
		return fmt.Errorf("failed to download file: %s (%s)", string(bodyBytes), resp.Status)
	} else {
		fmt.Printf("Скачан пакета %s\n", filePath)
	}

	out, err := os.Create(filePath)
	if err != nil {
		return err
	}
	defer out.Close()

	_, err = io.Copy(out, resp.Body)
	return err
}

func uploadFile(url, apiKey, filePath string) error {
	fmt.Printf("Загрузка пакета %s\n", filePath)
	file, err := os.Open(filePath)
	if err != nil {
		return err
	}
	defer file.Close()

	req, err := http.NewRequest("POST", url, file)
	if err != nil {
		return err
	}
	req.Header.Set("X-ApiKey", apiKey)

	client := &http.Client{}
	resp, err := client.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()

	if resp.StatusCode != 201 {
		bodyBytes, err := ioutil.ReadAll(resp.Body)
		if err != nil {
			return fmt.Errorf("cant read body from request. Failed to upload file: %s", resp.Status)
		}
		return fmt.Errorf("failed to upload file: %s (%s)", string(bodyBytes), resp.Status)
	} else {
		fmt.Printf("Загружен пакет %s\n", filePath)
	}

	return nil
}

func deleteFile(config *Config, pkg Package, version string) error {
	fmt.Printf("Пакет: %s/%s помечен для удаления из %s\n", pkg.Group, pkg.Name, config.Destination.URL)

	deleteURL := fmt.Sprintf("%s/upack/%s/delete/%s/%s/%s", config.Destination.URL, config.Destination.Feed, pkg.Group, pkg.Name, version)
	req, err := http.NewRequest("POST", deleteURL, nil)
	if err != nil {
		return err
	}
	req.Header.Set("X-ApiKey", config.Destination.APIKey)

	client := &http.Client{}
	resp, err := client.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		bodyBytes, err := ioutil.ReadAll(resp.Body)
		if err != nil {
			return fmt.Errorf("cant read body from request. Failed to delete file: %s", resp.Status)
		}
		return fmt.Errorf("failed to delete file: %s (%s)", string(bodyBytes), resp.Status)
	} else {
		fmt.Printf("Пакет: %s/%s удалён из %s\n", pkg.Group, pkg.Name, config.Destination.URL)
	}

	return err
}
