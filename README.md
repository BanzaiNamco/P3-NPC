
# Networked Producer and Consumer

# Building the Program
## Consumer
1. Navigate to the Consumer project `/P3-NPC/Consumer` and launch a terminal using this directory
2. Execute `dotnet run --launch-profile https`
3. A new tab with the Consumer page would be loaded.
	3.a. If no tab is opened, enter `https://localhost:7280/`
4. Configure the Consumer using the web page.

## Producer
1. Navigate to the Consumer project `/P3-NPC/Producer` and launch a terminal using this directory
2. Execute `dotnet run`
3. The program should run in the same window as your terminal.

# Uploading Videos
#### The Consumer must be running and configured.
1. Upon building the Producer, 8 folders in the `/P3-NPC/Producer/Videos/` should be created
	1.a. If no folders are created, they may manually be created using the folder name format `Thread <n>` where `n` is from 1 to 8
2. Videos may be inserted in any folder.
3. After inputting the desired threads in the Producer, the program will upload the videos to the Consumer.
	
