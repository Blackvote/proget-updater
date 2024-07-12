package main

import (
	"github.com/rs/zerolog/log"
	"gopkg.in/yaml.v2"
	"io"
	"io/ioutil"
	"os"
	"path/filepath"
	"strings"
)

type Config struct {
	SyncChain           []SyncChain     `yaml:"syncChain"`
	Timeout             TimeoutConfig   `yaml:"timeout"`
	ProceedPackageLimit int             `yaml:"proceedPackageLimit"`
	Retention           RetentionConfig `yaml:"retention"`
}

type SyncChain struct {
	Source      ProgetConfig `yaml:"source"`
	Destination ProgetConfig `yaml:"destination"`
	Type        string       `yaml:"type"`
}

type ProgetConfig struct {
	URL    string `yaml:"url"`
	APIKey string `yaml:"apiKey"`
	Feed   string `yaml:"feed"`
	Type   string `yaml:"type"`
}

type Package struct {
	Group    string   `yaml:"group"`
	Name     string   `yaml:"name"`
	Versions []string `yaml:"versions"`
}

type Asset struct {
	Name string `yaml:"name"`
	Type string `yaml:"type"`
}

type TimeoutConfig struct {
	WebRequestTimeout int `yaml:"webRequestTimeout"`
	IterationTimeout  int `yaml:"iterationTimeout"`
	SyncTimeout       int `yaml:"syncTimeout"`
}

type RetentionConfig struct {
	Enabled      bool `yaml:"enabled"`
	DryRun       bool `yaml:"dry-run"`
	VersionLimit int  `yaml:"versionLimit"`
}

func readConfig(configFile string) (*Config, error) {

	if _, err := os.Stat(configFile); os.IsNotExist(err) {
		return nil, err
	}

	data, err := ioutil.ReadFile(configFile)
	if err != nil {
		return nil, err
	}

	var config Config
	err = yaml.Unmarshal(data, &config)
	if err != nil {
		return nil, err
	}

	for i := range config.SyncChain {
		config.SyncChain[i].Source.URL = strings.TrimSuffix(config.SyncChain[i].Source.URL, "/")
		config.SyncChain[i].Destination.URL = strings.TrimSuffix(config.SyncChain[i].Destination.URL, "/")

		config.SyncChain[i].Source.Type = config.SyncChain[i].Type
		config.SyncChain[i].Destination.Type = config.SyncChain[i].Type
	}

	return &config, nil
}

func setupLogging(logFilePath string) (*os.File, error) {
	logFile, err := os.OpenFile(logFilePath, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0666)
	if err != nil {
		return nil, err
	}

	multiWriter := io.MultiWriter(os.Stdout, logFile)
	log.Logger = log.Output(multiWriter).With().
		Str("app", "Updater").
		Logger()
	return logFile, nil
}

func createDeleteDirectoryContents(dir string) error {

	_, err := os.Stat(dir)
	if os.IsNotExist(err) {
		err := os.Mkdir(dir, 0755)
		if err != nil {
			return err
		}
	} else if err != nil {
		return err
	} else {
	}

	files, err := ioutil.ReadDir(dir)
	if err != nil {
		return err
	}

	for _, file := range files {
		filePath := filepath.Join(dir, file.Name())
		if file.IsDir() {
			err = os.RemoveAll(filePath)
		} else {
			err = os.Remove(filePath)
		}
		if err != nil {
			return err
		}
	}

	return nil
}

func cleanURL(url string) string {
	parts := strings.SplitN(url, "://", 2)
	if len(parts) != 2 {
		return url
	}
	parts[1] = strings.ReplaceAll(parts[1], "//", "/")
	return parts[0] + "://" + parts[1]
}
