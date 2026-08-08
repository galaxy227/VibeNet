#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>

#include <sys/types.h>
#include <sys/socket.h>
#include <netinet/in.h>
#include <netdb.h>

#define PORT "3490"
#define BACKLOG 10

/*
 * ####################################
 * ERROR
 * ####################################
*/

enum ERROR_CODE {
	ERR_CMDLINEARG_COUNT = 1,
	ERR_CMDLINEARG_NETMODE,
	ERR_CMDLINEARG_IP,
	ERR_CMDLINEARG_PORT,
	ERR_NETWORK,
};

struct Error {
	enum ERROR_CODE code;
	const char* message;
};

const int error_arr_size = 2;
struct Error error_arr[] = {
	{ERR_CMDLINEARG_COUNT, "Invalid command line argument count. Expected arguments listed below:\n1. Network mode\n2. IP Address\n3. Port number"},
	{ERR_CMDLINEARG_NETMODE, "Invalid input for first command line argument. Expected options listed below:\n1. 's' for server\n2. 'c' for client"},
	{ERR_CMDLINEARG_IP, "Invalid input for second command line argument. Expected IP Address"},
	{ERR_CMDLINEARG_PORT, "Invalid input for third command line argument. Expected integer for port number"},
	{ERR_NETWORK, "Error related to network."},
};

void exit_error(enum ERROR_CODE e, const char* m) {
	// Get index to error_arr
	int index = -1;
	for (int i = 0; i < error_arr_size; i++) {
		if (e == error_arr[i].code) {
			index = i;
			break;
		}
	}
	if (index == -1) {
		fprintf(stderr, "Unknown error\n");
		exit(-1);
	}

	// 

	if (!m) {

	} else {

	}
	struct Error* error = &error_arr[index];
	fprintf(stderr, "%s\n%s\n", error->message, m);
	exit((int)error->code);
}

/*
 * ####################################
 * NETWORK
 * ####################################
*/

void *get_in_addr(struct sockaddr *sa) {
	if (sa->sa_family == AF_INET) return &(((struct sockaddr_in*)sa)->sin_addr);
	else return &(((struct sockaddr_in6*)sa)->sin6_addr);
}

/*
 * ####################################
 * UTILITY
 * ####################################
*/

int is_integer(const char *s) {
	if (!s || !*s) return 0;
	for (; *s; s++) {
		if (!isdigit((unsigned char)*s))
		return 0;
	}
	return 1;
}

int get_string_size(const char* s) {
	if (!s) return 0;

	int size = 0;
	while (s[size] != '\0') {
		size++;
	}
	return size;
}
const char* concatenator(const char* a, const char* b) {
	char *result = malloc(strlen(a) + strlen(b) + 1);
	if (!result) return NULL;
	strcpy(result, a);
	strcat(result, b);
}

/*
 * ####################################
 * MAIN
 * ####################################
*/

int main(int argc, char* argv[]) {
	if (argc != 4) exit_error(ERR_CMDLINEARG_COUNT, NULL);

	if (argv[1] != "s" || argv[1] != "c") exit_error(ERR_CMDLINEARG_NETMODE, NULL);
	// TODO argv[2] IP Address
	if (!is_integer(argv[3])) exit_error(ERR_CMDLINEARG_PORT, NULL);

	/*
	int status = getaddrinfo(NULL, );
	if (status != 0) exit_error(ERR_NETWORK, gai_strerror(status));
	*/

	return 0;
}
