package main

import (
	"bytes"
	"context"
	"encoding/json"
	"encoding/xml"
	"fmt"
	"github.com/rs/zerolog/log"
	"io"
	"io/ioutil"
	"mime/multipart"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

func getPackages(ctx context.Context, progetConfig ProgetConfig, timeoutConfig TimeoutConfig) ([]Package, error) {
	var (
		url      string
		resp     *http.Response
		packages []Package
		assets,
		allAssets []Asset
	)

	if progetConfig.Type == "asset" {
		url = fmt.Sprintf("%s/endpoints/%s/dir", progetConfig.URL, progetConfig.Feed)
	} else {
		url = fmt.Sprintf("%s/%s/%s/packages", progetConfig.URL, progetConfig.Type, progetConfig.Feed)
	}

	req, err := http.NewRequestWithContext(ctx, "GET", url, nil)
	if err != nil {
		return nil, err
	}

	req.Header.Set("X-ApiKey", progetConfig.APIKey)
	client := &http.Client{
		Timeout: time.Duration(timeoutConfig.IterationTimeout) * time.Second,
	}

	for attempt := 1; attempt <= maxRetries; attempt++ {
		log.Info().Str("url", url).Msgf("Attempt %d to get package list", attempt)
		resp, bodyStr, err := apiCall(client, req)

		if err != nil {
			if bodyStr != "" {
				log.Error().Err(err).Str("url", url).Msgf("Attempt %d failed to get package. Body: %s", attempt, bodyStr)
			} else {
				log.Error().Err(err).Str("url", url).Msgf("Attempt %d failed to get package.", attempt)
			}
			time.Sleep(3 * time.Duration(attempt) * time.Second)
			continue
		}

		if err == nil && resp.StatusCode == http.StatusOK {
			switch progetConfig.Type {
			case "upack":
				err = json.NewDecoder(strings.NewReader(bodyStr)).Decode(&packages)
				if err != nil {
					log.Error().Err(err).Str("url", url).Msgf("error reading response")
					time.Sleep(3 * time.Duration(attempt) * time.Second)
					continue
				}
			case "nuget":
				packages, err = decodeXML(bodyStr)
				if err != nil {
					log.Error().Err(err).Str("url", url).Msgf("error reading response")
					time.Sleep(3 * time.Duration(attempt) * time.Second)
					continue
				}
			case "asset":
				err = json.NewDecoder(strings.NewReader(bodyStr)).Decode(&assets)
				for _, asset := range assets {
					if asset.Type == "dir" {
						subAssets, err := fetchAssets(url+"/"+asset.Name, asset.Name)
						if err != nil {
							return nil, err
						}
						allAssets = append(allAssets, subAssets...)
					} else {
						allAssets = append(allAssets, asset)
					}
				}
				packages = make([]Package, len(allAssets))
				for i, asset := range allAssets {
					packages[i] = Package{
						Group:    "",
						Name:     asset.Name,
						Versions: []string{"0"},
					}
				}
			}
			return packages, nil
		}

		log.Info().Str("url", url).Msgf("Package count in %s/%s: %d", progetConfig.URL, progetConfig.Feed, len(packages))

		var packageList strings.Builder
		for _, pkg := range packages {
			packageList.WriteString(fmt.Sprintf("%s/%s: %s | ", pkg.Group, pkg.Name, strings.Join(pkg.Versions, " ")))
		}

		log.Info().Str("url", url).Msg(packageList.String())
	}
	if err != nil {
		log.Error().Err(err).Str("url", url).Msgf("Failed to get package")
	}
	if resp != nil && err == nil {
		log.Error().Str("url", url).Msgf("Failed to get package, Status: %s.", resp.Status)
	}
	return nil, fmt.Errorf("failed to get package after %d attempts", maxRetries)
}

func SyncPackages(ctx context.Context, config *Config, chain SyncChain, sourcePackages, destPackages []Package, savePath string) error {
	sourcePackageMap := make(map[string]map[string]bool)
	for _, pkg := range sourcePackages {
		key := fmt.Sprintf("%s:%s", pkg.Group, pkg.Name)
		if sourcePackageMap[key] == nil {
			sourcePackageMap[key] = make(map[string]bool)
		}
		for i, version := range pkg.Versions {
			if !config.Retention.Enabled {
				sourcePackageMap[key][version] = true
			} else {
				if i <= config.Retention.VersionLimit-1 {
					sourcePackageMap[key][version] = true
				} else {
					if !config.Retention.DryRun {
						sourcePackageMap[key][version] = false
					} else {
						log.Warn().Str("url", chain.Destination.URL).Msgf("%s:%s exceedes version limit, will be processed (dry-run is on)", key, version)
						sourcePackageMap[key][version] = true
					}
				}
			}
		}
	}

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

	if len(sourcePackageMap) < config.ProceedPackageLimit {
		config.ProceedPackageLimit = len(sourcePackageMap)
	}
	semaphore := make(chan struct{}, config.ProceedPackageLimit)
	var wg sync.WaitGroup
	for _, pkg := range sourcePackages {
		for _, version := range pkg.Versions {
			key := fmt.Sprintf("%s:%s", pkg.Group, pkg.Name)
			if !destPackageMap[key][version] {
				if sourcePackageMap[key][version] {
					log.Info().Str("url", chain.Destination.URL).Msgf("%s:%s:%s not found. Syncing", pkg.Group, pkg.Name, version)
					wg.Add(1)
					go func(pkg Package, version string) {
						defer wg.Done()
						semaphore <- struct{}{}
						defer func() { <-semaphore }()
						err := downloadAndUploadPackage(ctx, config, chain, pkg, version, savePath)
						if err != nil {
							log.Error().Err(err).Str("url", chain.Destination.URL).Msgf("Failed to Sync package %s:%s", pkg.Name, version)
						}
					}(pkg, version)
				}
			} else {
				log.Info().Str("url", chain.Destination.URL).Msgf("%s:%s:%s found.", pkg.Group, pkg.Name, version)
			}
		}
	}
	wg.Wait()
	return nil
}

func downloadAndUploadPackage(ctx context.Context, config *Config, chain SyncChain, pkg Package, version, savePath string) error {
	var (
		downloadURL,
		uploadURL,
		filePath string
	)
	switch chain.Type {
	case "upack":
		downloadURL = fmt.Sprintf("%s/%s/%s/download/%s/%s/%s", chain.Source.URL, chain.Type, chain.Source.Feed, pkg.Group, pkg.Name, version)
		uploadURL = fmt.Sprintf("%s/%s/%s/upload", chain.Destination.URL, chain.Type, chain.Destination.Feed)
		filePath = filepath.Join(savePath, fmt.Sprintf("%s.%s.upack", pkg.Name, version))
	case "nuget":
		downloadURL = fmt.Sprintf("%s/%s/%s/package/%s/%s", chain.Source.URL, chain.Type, chain.Source.Feed, pkg.Name, version)
		uploadURL = fmt.Sprintf("%s/%s/%s/upload", chain.Destination.URL, chain.Type, chain.Destination.Feed)
		filePath = filepath.Join(savePath, fmt.Sprintf("%s.%s.nupkg", pkg.Name, version))
	case "asset":
		downloadURL = fmt.Sprintf("%s/endpoints/%s/content/%s", chain.Source.URL, chain.Source.Feed, pkg.Name)
		uploadURL = fmt.Sprintf("%s/endpoints/%s/content/%s", chain.Destination.URL, chain.Destination.Feed, pkg.Name)
		filePath = filepath.Join(savePath, pkg.Name)
	}

	err := os.MkdirAll(savePath, os.ModePerm)
	if err != nil {
		log.Error().Err(err).Msgf("Failed to create dir %s", savePath)
	}

	err = downloadFile(ctx, downloadURL, chain.Source.APIKey, filePath, config.Timeout)
	if err != nil {
		return err
	}

	return uploadFile(ctx, uploadURL, chain.Destination.APIKey, filePath, config.Timeout)
}

func downloadFile(ctx context.Context, url, apiKey, filePath string, timeoutConfig TimeoutConfig) error {
	log.Info().Str("url", url).Msgf("Download package %s", filePath)

	req, err := http.NewRequestWithContext(ctx, "GET", url, nil)
	if err != nil {
		return err
	}
	req.Header.Set("X-ApiKey", apiKey)
	client := &http.Client{
		Timeout: time.Duration(timeoutConfig.WebRequestTimeout) * time.Second,
	}

	var resp *http.Response
	for attempt := 1; attempt <= maxRetries; attempt++ {

		log.Info().Str("url", url).Msgf("Attempt download %d file %s", attempt, filePath)

		resp, bodyStr, err := apiCall(client, req)

		if err != nil {
			if bodyStr != "" {
				log.Error().Err(err).Str("url", url).Msgf("Attempt %d. Failed to download %s. Body: %s", attempt, filePath, bodyStr)
			} else {
				log.Error().Err(err).Str("url", url).Msgf("Attempt %d. Failed to download %s", attempt, filePath)
			}
			time.Sleep(3 * time.Duration(attempt) * time.Second)
			continue
		}

		if err == nil && resp.StatusCode == http.StatusOK {
			dir := filepath.Dir(filePath)
			err := os.MkdirAll(dir, os.ModePerm)
			if err != nil {
				fmt.Println("Error creating directories:", err)
			}

			out, err := os.Create(filePath)
			if err != nil {
				fmt.Println("Error creating file:", err)
				time.Sleep(3 * time.Duration(attempt) * time.Second)
				continue
			}

			if err != nil {
				log.Error().Err(err).Str("url", url).Msgf("Failed to create file %s", filePath)
				time.Sleep(3 * time.Duration(attempt) * time.Second)
				continue
			}

			fileSize, err := io.Copy(out, strings.NewReader(bodyStr))
			if err != nil {
				log.Error().Err(err).Str("url", url).Msgf("Failed to copy response body")
				time.Sleep(3 * time.Duration(attempt) * time.Second)
				continue
			}

			out.Close()
			fileSizeMB := float64(fileSize) / (1024 * 1024)
			log.Info().Str("url", url).Msgf("%s file Size: %.2f MB", strings.TrimSuffix(strings.TrimPrefix(filePath, "packages\\"), ".upack"), fileSizeMB)
			log.Info().Str("url", url).Msgf("Success download %s", strings.TrimPrefix(filePath, "packages\\"))
			return nil
		}

		time.Sleep(3 * time.Duration(attempt) * time.Second)
	}
	if resp != nil {
		return fmt.Errorf("failed to download %s after %d attempts. Status: %s", strings.TrimSuffix(strings.TrimPrefix(filePath, "packages\\"), ".upack"), maxRetries, resp.Status)
	}

	if err != nil {
		return fmt.Errorf("failed to download %s after %d attempts. Error: %s", strings.TrimSuffix(strings.TrimPrefix(filePath, "packages\\"), ".upack"), maxRetries, err)
	}
	return nil
}

func uploadFile(ctx context.Context, url, apiKey, filePath string, timeoutConfig TimeoutConfig) error {
	log.Info().Str("url", url).Msgf("Upload package %s", strings.TrimSuffix(strings.TrimPrefix(filePath, "packages\\"), ".upack"))
	file, err := os.Open(filePath)
	if err != nil {
		return err
	}
	defer file.Close()

	body := &bytes.Buffer{}
	writer := multipart.NewWriter(body)

	part, err := writer.CreateFormFile("filename", filePath)
	if err != nil {
		return fmt.Errorf("failed to create form file: %w", err)
	}

	_, err = io.Copy(part, file)
	if err != nil {
		return fmt.Errorf("failed to copy file content: %w", err)
	}

	err = writer.Close()
	if err != nil {
		return fmt.Errorf("failed to close writer: %w", err)
	}

	client := &http.Client{
		Timeout: time.Duration(timeoutConfig.WebRequestTimeout) * time.Second,
	}
	req, err := http.NewRequestWithContext(ctx, "PUT", url, body)
	if err != nil {
		return fmt.Errorf("failed to create request: %w", err)
	}

	req.Header.Add("X-ApiKey", apiKey)
	req.Header.Set("Content-Type", writer.FormDataContentType())

	var resp *http.Response
	for attempt := 1; attempt <= maxRetries; attempt++ {
		log.Info().Str("url", url).Msgf("Attempt %d upload for file %s", attempt, strings.TrimSuffix(strings.TrimPrefix(filePath, "packages\\"), ".upack"))

		resp, bodyStr, err := apiCall(client, req)
		if err != nil {
			if bodyStr != "" {
				log.Error().Err(err).Str("url", url).Msgf("Attempt %d. Failed to upload %s. Body: %s", attempt, filePath, bodyStr)
			} else {
				log.Error().Err(err).Str("url", url).Msgf("Attempt %d. Failed to upload %s", attempt, filePath)
			}
			time.Sleep(3 * time.Duration(attempt) * time.Second)
			continue
		}

		if resp == nil && err != nil {
			log.Info().Str("url", url).Msgf("Failed %d attempt. Cant get responce. Error: %s", attempt, err)
			time.Sleep(3 * time.Duration(attempt) * time.Second)
			continue
		}

		if resp.StatusCode == http.StatusCreated {
			log.Info().Str("url", url).Msgf("Success upload: for file %s", strings.TrimSuffix(strings.TrimPrefix(filePath, "packages\\"), ".upack"))
			err = os.Remove(filePath)
			return nil
		}

		log.Warn().Str("url", url).Msgf("Failed %d attempt. Status Code: %d. Body: %s", attempt, resp.StatusCode, bodyStr)
		time.Sleep(3 * time.Duration(attempt) * time.Second)
		continue
	}

	if resp != nil {
		return fmt.Errorf("failed to upload %s after %d attempts. Status: %s", strings.TrimSuffix(strings.TrimPrefix(filePath, "packages\\"), ".upack"), maxRetries, resp.Status)
	}

	if err != nil {
		return fmt.Errorf("failed to upload %s after %d attempts. Error: %s", strings.TrimSuffix(strings.TrimPrefix(filePath, "packages\\"), ".upack"), maxRetries, err)
	}
	return fmt.Errorf("failed to upload %s after %d attempts", strings.TrimSuffix(strings.TrimPrefix(filePath, "packages\\"), ".upack"), maxRetries)
}

func decodeXML(bodyStr string) ([]Package, error) {
	var packages []Package
	decoder := xml.NewDecoder(strings.NewReader(bodyStr))
	for {
		t, err := decoder.Token()
		if err != nil {
			if err == io.EOF {
				break
			}
			return nil, err
		}
		switch se := t.(type) {
		case xml.StartElement:
			if se.Name.Local == "id" {
				var id string
				err := decoder.DecodeElement(&id, &se)
				if err != nil {
					return nil, err
				}
				parts := strings.Split(id, "Packages(Id='")
				if len(parts) > 1 {
					parts = strings.Split(parts[1], "',Version='")
					if len(parts) > 1 {
						name := parts[0]
						version := strings.TrimSuffix(parts[1], "')")

						// Обновляем существующий пакет или добавляем новый
						found := false
						for i, pkg := range packages {
							if pkg.Name == name {
								packages[i].Versions = append(packages[i].Versions, version)
								found = true
								break
							}
						}
						if !found {
							pkg := Package{
								Group:    "",
								Name:     name,
								Versions: []string{version},
							}
							packages = append(packages, pkg)
						}
					}
				}
			}
		}
	}
	return packages, nil
}

func fetchAssets(url string, parentPath string) ([]Asset, error) {
	var allAssets []Asset

	client := &http.Client{}
	req, err := http.NewRequest("GET", url, nil)
	if err != nil {
		return nil, err
	}

	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Authorization", "Bearer f12ac9062f385a9fa4b77bb110237db6f8e7e863")

	resp, err := client.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("error fetching assets: %v", resp.Status)
	}

	body, err := ioutil.ReadAll(resp.Body)
	if err != nil {
		return nil, err
	}

	var assets []Asset
	err = json.Unmarshal(body, &assets)
	if err != nil {
		return nil, err
	}

	for _, asset := range assets {
		fullName := parentPath + "/" + asset.Name
		if asset.Type == "dir" {
			subAssets, err := fetchAssets(url+"/"+asset.Name, fullName)
			if err != nil {
				return nil, err
			}
			allAssets = append(allAssets, subAssets...)
		} else {
			asset.Name = fullName
			allAssets = append(allAssets, asset)
		}
	}

	return allAssets, nil
}

func apiCall(client *http.Client, req *http.Request) (*http.Response, string, error) {
	resp, err := client.Do(req)
	if err != nil {
		return nil, "", err
	}
	defer resp.Body.Close()

	bodyBytes, err := io.ReadAll(resp.Body)
	if err != nil {
		return nil, "", err
	}
	bodyString := string(bodyBytes)

	return resp, bodyString, nil
}
