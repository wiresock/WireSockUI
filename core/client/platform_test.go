package main

import "runtime"

var isWindowsBuild = runtime.GOOS == "windows"
