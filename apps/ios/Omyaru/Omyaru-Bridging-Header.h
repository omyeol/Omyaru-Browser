#ifndef OMYARU_BRIDGING_HEADER_H
#define OMYARU_BRIDGING_HEADER_H

#include <stdint.h>

int32_t omyaru_wg_start(const char *configText);
void omyaru_wg_stop(void);
int32_t omyaru_wg_port(void);
char *omyaru_wg_last_error(void);
char *omyaru_wg_stats_json(void);
void omyaru_wg_free_string(char *s);

#endif
