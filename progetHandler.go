package main

import (
	"context"
	"encoding/json"
	"encoding/xml"
	"fmt"
	"github.com/rs/zerolog/log"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"time"
)

func getPackages(ctx context.Context, progetConfig ProgetConfig, timeoutConfig TimeoutConfig) ([]Package, error) {
	var (
		url      string
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
		resp, bodyStr, err := apiCallReadBody(client, req)

		if err != nil || resp.StatusCode != http.StatusOK {
			if bodyStr != "" {
				log.Error().Err(err).Str("url", url).Msgf("Attempt %d failed to get package. Status: %s", attempt, resp.Status)
			} else {
				log.Error().Err(err).Str("url", url).Msgf("Attempt %d failed to get package.", attempt)
			}
			time.Sleep(3 * time.Duration(attempt) * time.Second)
			continue
		}

		if resp.StatusCode == http.StatusOK {
			switch progetConfig.Type {
			case "upack":
				err = json.NewDecoder(strings.NewReader(bodyStr)).Decode(&packages)
				if err != nil {
					log.Error().Err(err).Str("url", url).Msgf("error decoding package list")
					time.Sleep(3 * time.Duration(attempt) * time.Second)
					continue
				}
			case "nuget":
				packages, err = decodeXML(bodyStr)
				if err != nil {
					log.Error().Err(err).Str("url", url).Msgf("error decoding package list")
					time.Sleep(3 * time.Duration(attempt) * time.Second)
					continue
				}
			case "asset":
				err = json.NewDecoder(strings.NewReader(bodyStr)).Decode(&assets)
				if err != nil {
					log.Error().Err(err).Str("url", url).Msgf("error decoding package list")
					time.Sleep(3 * time.Duration(attempt) * time.Second)
					continue
				}
				for _, asset := range assets {
					if asset.Type == "dir" {
						subAssets, err := fetchAssets(client, url+"/"+asset.Name, asset.Name, progetConfig.APIKey)
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

		}

		log.Info().Str("url", url).Msgf("Package count: %d", len(packages))
		return packages, nil
	}
	return nil, fmt.Errorf("failed to get package after %d attempts", maxRetries)
}

func getPackagesToSync(config *Config, chain SyncChain, sourcePackages, destPackages []Package) ([]Package, error) {
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

	packagesToSyncMap := make(map[string]*Package)
	for _, pkg := range sourcePackages {
		for _, version := range pkg.Versions {
			key := fmt.Sprintf("%s:%s", pkg.Group, pkg.Name)
			if !destPackageMap[key][version] {
				if sourcePackageMap[key][version] {
					log.Printf("%s:%s:%s not found.", pkg.Group, pkg.Name, version)
					if existingPkg, exists := packagesToSyncMap[key]; exists {
						existingPkg.Versions = append(existingPkg.Versions, version)
					} else {
						packagesToSyncMap[key] = &Package{
							Group:    pkg.Group,
							Name:     pkg.Name,
							Versions: []string{version},
						}
					}
				}
			}
			//else {
			//	log.Printf("%s:%s:%s found.", pkg.Group, pkg.Name, version)
			//}
		}
	}

	packagesToSync := make([]Package, 0, len(packagesToSyncMap))
	for _, pkg := range packagesToSyncMap {
		packagesToSync = append(packagesToSync, *pkg)
	}

	return packagesToSync, nil
}

func downloadAndUploadPackage(ctx context.Context, config *Config, chain SyncChain, pkg Package, version, savePath string) error {
	var (
		downloadURL,
		uploadURL,
		filePath string
	)
	if *debug {
		log.Info().Str("url", chain.Source.URL).Msgf("Switch to choose urls. case: %s", chain.Destination.Type)
	}
	switch chain.Type {
	case "upack":
		downloadURL = cleanURL(fmt.Sprintf("%s/%s/%s/download/%s/%s/%s", chain.Source.URL, chain.Type, chain.Source.Feed, pkg.Group, pkg.Name, version))
		uploadURL = cleanURL(fmt.Sprintf("%s/%s/%s/upload", chain.Destination.URL, chain.Type, chain.Destination.Feed))
		filePath = filepath.Join(savePath, fmt.Sprintf("%s.%s.upack", pkg.Name, version))
	case "nuget":
		downloadURL = cleanURL(fmt.Sprintf("%s/%s/%s/package/%s/%s", chain.Source.URL, chain.Type, chain.Source.Feed, pkg.Name, version))
		uploadURL = cleanURL(fmt.Sprintf("%s/%s/%s/upload", chain.Destination.URL, chain.Type, chain.Destination.Feed))
		filePath = filepath.Join(savePath, fmt.Sprintf("%s.%s.nupkg", pkg.Name, version))
	case "asset":
		downloadURL = cleanURL(fmt.Sprintf("%s/endpoints/%s/content/%s", chain.Source.URL, chain.Source.Feed, pkg.Name))
		uploadURL = cleanURL(fmt.Sprintf("%s/endpoints/%s/content/%s", chain.Destination.URL, chain.Destination.Feed, pkg.Name))
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

	for attempt := 1; attempt <= maxRetries; attempt++ {
		log.Info().Str("url", url).Msgf("Attempt download %d file %s", attempt, filePath)

		resp, err := apiCall(client, req)
		if err != nil || resp.StatusCode != http.StatusOK {
			log.Error().Err(err).Str("url", url).Msgf("Attempt %d. Failed to download %s. Status: %s", attempt, filePath, resp.Status)
			time.Sleep(3 * time.Duration(attempt) * time.Second)
			continue
		}

		if resp.StatusCode == http.StatusOK {
			dir := filepath.Dir(filePath)
			err := os.MkdirAll(dir, os.ModePerm)
			if err != nil {
				fmt.Println("Error creating directories:", err)
			}

			if *debug {
				log.Debug().Str("url", req.URL.String()).Msgf("creating empty file %s", filePath)
			}

			out, err := os.Create(filePath)
			if err != nil {
				fmt.Println("Error creating file:", err)
				time.Sleep(3 * time.Duration(attempt) * time.Second)
				continue
			}

			if *debug {
				log.Debug().Str("url", req.URL.String()).Msgf("Copy bytes in file %s", filePath)
			}

			fileSize, err := io.Copy(out, resp.Body)
			if err != nil {
				log.Error().Err(err).Str("url", url).Msgf("Failed to copy response body")
				time.Sleep(3 * time.Duration(attempt) * time.Second)
				continue
			}

			out.Close()
			fileSizeMB := float64(fileSize) / (1024 * 1024)
			log.Info().Str("url", url).Msgf("Success download %s. File Size: %.2f MB", strings.TrimPrefix(filePath, "packages\\"), fileSizeMB)
			return nil
		}
		time.Sleep(3 * time.Duration(attempt) * time.Second)
		continue
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

	client := &http.Client{
		Timeout: time.Duration(timeoutConfig.WebRequestTimeout) * time.Second,
	}

	if *debug {
		log.Debug().Str("url", url).Msgf("create upload reqeest. File: %s", filePath)
	}

	req, err := http.NewRequestWithContext(ctx, "PUT", url, file)
	if err != nil {
		return fmt.Errorf("failed to create request: %w", err)
	}

	req.Header.Add("X-ApiKey", apiKey)

	for attempt := 1; attempt <= maxRetries; attempt++ {
		log.Info().Str("url", url).Msgf("Attempt %d upload for file %s", attempt, strings.TrimSuffix(strings.TrimPrefix(filePath, "packages\\"), ".upack"))

		resp, err := apiCall(client, req)
		if err != nil || resp.StatusCode != http.StatusCreated {
			log.Error().Err(err).Str("url", url).Msgf("Attempt %d. Failed to upload %s", attempt, filePath)
			time.Sleep(3 * time.Duration(attempt) * time.Second)
			continue
		}

		if resp.StatusCode == http.StatusCreated {
			log.Info().Str("url", url).Msgf("Success upload: for file %s", strings.TrimSuffix(strings.TrimPrefix(filePath, "packages\\"), ".upack"))
			err = os.Remove(filePath)
			return nil
		}

		log.Warn().Str("url", url).Msgf("Failed %d attempt. Status Code: %d.", attempt, resp.StatusCode)
		time.Sleep(3 * time.Duration(attempt) * time.Second)
		continue
	}
	return fmt.Errorf("failed to upload %s after %d attempts", strings.TrimSuffix(strings.TrimPrefix(filePath, "packages\\"), ".upack"), maxRetries)
}

// gpt-4o
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

func fetchAssets(client *http.Client, url string, parentPath, apiKey string) ([]Asset, error) {
	var allAssets []Asset

	req, err := http.NewRequest("GET", url, nil)
	if err != nil {
		return nil, err
	}

	req.Header.Add("X-ApiKey", apiKey)

	resp, err := client.Do(req)
	if err != nil {
		return nil, err
	}
	defer func(Body io.ReadCloser) {
		err := Body.Close()
		if err != nil {

		}
	}(resp.Body)

	body, err := io.ReadAll(resp.Body)
	if err != nil {
		return nil, err
	}

	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("error fetching assets: %v", resp.Status)
	}

	var assets []Asset
	err = json.Unmarshal(body, &assets)
	if err != nil {
		return nil, err
	}

	for _, asset := range assets {
		fullName := parentPath + "/" + asset.Name
		if asset.Type == "dir" {
			subAssets, err := fetchAssets(client, url+"/"+asset.Name, fullName, apiKey)
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

func apiCallReadBody(client *http.Client, req *http.Request) (*http.Response, string, error) {

	resp, err := client.Do(req)
	if err != nil {
		return nil, "", err
	}
	if *debug {
		log.Info().Str("url", req.URL.String()).Msgf("get resp %d", resp.StatusCode)
	}
	defer func(Body io.ReadCloser) {
		err := Body.Close()
		if err != nil {

		}
	}(resp.Body)

	if *debug {
		log.Info().Str("url", req.URL.String()).Msgf("Read body...")
	}
	bodyBytes, err := io.ReadAll(resp.Body)
	if err != nil {
		return nil, "", err
	}
	bodyString := string(bodyBytes)
	if *debug {
		log.Info().Str("url", req.URL.String()).Msgf("Body: %s", bodyString)
	}
	return resp, bodyString, nil
}

func apiCall(client *http.Client, req *http.Request) (*http.Response, error) {

	resp, err := client.Do(req)
	if err != nil {
		return nil, err
	}
	if *debug {
		log.Info().Str("url", req.URL.String()).Msgf("get resp %d", resp.StatusCode)
	}
	defer resp.Body.Close()

	if *debug {
		log.Info().Str("url", req.URL.String()).Msgf("Read body...")

		bodyBytes, err := io.ReadAll(resp.Body)
		if err != nil {
			return nil, err
		}
		bodyString := string(bodyBytes)
		log.Info().Str("url", req.URL.String()).Msgf("Body: %s", bodyString)
	}
	return resp, nil
}
