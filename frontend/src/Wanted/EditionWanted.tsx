import React, { useCallback, useEffect, useState } from 'react';
import Alert from 'Components/Alert';
import SelectInput from 'Components/Form/SelectInput';
import Label from 'Components/Label';
import Button from 'Components/Link/Button';
import Link from 'Components/Link/Link';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageToolbar from 'Components/Page/Toolbar/PageToolbar';
import PageToolbarButton from 'Components/Page/Toolbar/PageToolbarButton';
import PageToolbarSection from 'Components/Page/Toolbar/PageToolbarSection';
import RelativeDateCell from 'Components/Table/Cells/RelativeDateCell';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import TableRow from 'Components/Table/TableRow';
import useApiQuery from 'Helpers/Hooks/useApiQuery';
import { icons, kinds } from 'Helpers/Props';
import MovieEdition from 'Movie/MovieEdition';
import MovieQuality from 'Movie/MovieQuality';
import { InputChanged } from 'typings/inputs';
import translate from 'Utilities/String/translate';
import styles from './EditionWanted.css';

interface WantedEdition extends MovieEdition {
  title: string;
  year: number;
  tmdbId: number;
  movieMonitored: boolean;
  lastSearchTime?: string;
}

interface WantedPage {
  totalRecords: number;
  records: WantedEdition[];
}

const columns = [
  { name: 'movie', label: () => translate('Movie'), isVisible: true },
  { name: 'edition', label: () => translate('Edition'), isVisible: true },
  {
    name: 'attributes',
    label: () => translate('EditionAttributes'),
    isVisible: true,
  },
  {
    name: 'profile',
    label: () => translate('QualityProfile'),
    isVisible: true,
  },
  { name: 'monitoring', label: () => translate('Monitored'), isVisible: true },
  { name: 'file', label: () => translate('File'), isVisible: true },
  {
    name: 'lastSearch',
    label: () => translate('LastSearched'),
    isVisible: true,
  },
];

function WantedEditionFile({ edition }: { edition: WantedEdition }) {
  if (edition.state === 'invalid') {
    return <Alert kind={kinds.DANGER}>{edition.rejectionReason}</Alert>;
  }
  if (edition.quality) {
    return (
      <>
        <MovieQuality quality={edition.quality} />
        <div>{edition.relativePath}</div>
      </>
    );
  }
  return <>{translate('EditionMissing')}</>;
}

function EditionWanted() {
  const [kind, setKind] = useState('missing');
  const [monitored, setMonitored] = useState('true');
  const [sort, setSort] = useState('title');
  const [page, setPage] = useState(1);
  const { data, error, isPending, isFetching, refetch } =
    useApiQuery<WantedPage>({
      path: `/wanted/edition${kind}?page=${page}&pageSize=10&monitored=${monitored}&sortKey=${encodeURIComponent(
        sort
      )}&sortDirection=ascending`,
      queryOptions: { staleTime: 0, refetchOnMount: 'always', retry: false },
    });
  const pages = Math.max(1, Math.ceil((data?.totalRecords ?? 0) / 10));
  useEffect(() => {
    if (data && page > pages) {
      setPage(pages);
    }
  }, [data, page, pages]);
  const onKindChange = useCallback(({ value }: InputChanged<string>) => {
    setKind(value === 'cutoff' ? 'cutoff' : 'missing');
    setPage(1);
  }, []);
  const onMonitoringChange = useCallback(({ value }: InputChanged<string>) => {
    setMonitored(value === 'false' ? 'false' : 'true');
    setPage(1);
  }, []);
  const onSortChange = useCallback(({ value }: InputChanged<string>) => {
    setSort(value);
    setPage(1);
  }, []);
  const onRefresh = useCallback(() => {
    if (page === 1) {
      refetch();
    } else {
      setPage(1);
    }
  }, [page, refetch]);
  const onPrevious = useCallback(
    () => setPage((current) => Math.max(1, current - 1)),
    []
  );
  const onNext = useCallback(() => setPage((current) => current + 1), []);

  return (
    <PageContent title={translate('EditionWanted')}>
      <PageToolbar>
        <PageToolbarSection>
          <PageToolbarButton
            label={translate('Refresh')}
            iconName={icons.REFRESH}
            isSpinning={isFetching}
            onPress={onRefresh}
          />
        </PageToolbarSection>
      </PageToolbar>
      <PageContentBody>
        <p>{translate('EditionWantedHelpText')}</p>
        <div className={styles.controls}>
          <label>
            {translate('Status')}
            <SelectInput
              name="editionWantedStatus"
              value={kind}
              values={[
                { key: 'missing', value: translate('Missing') },
                { key: 'cutoff', value: translate('CutoffUnmet') },
              ]}
              onChange={onKindChange}
            />
          </label>
          <label>
            {translate('Monitored')}
            <SelectInput
              name="editionWantedMonitoring"
              value={monitored}
              values={[
                { key: 'true', value: translate('EffectivelyMonitored') },
                { key: 'false', value: translate('EffectivelyUnmonitored') },
              ]}
              onChange={onMonitoringChange}
            />
          </label>
          <label>
            {translate('Sort')}
            <SelectInput
              name="editionWantedSort"
              value={sort}
              values={[
                { key: 'title', value: translate('Title') },
                { key: 'year', value: translate('Year') },
                { key: 'editionName', value: translate('Edition') },
                { key: 'lastSearchTime', value: translate('LastSearched') },
              ]}
              onChange={onSortChange}
            />
          </label>
        </div>
        {kind === 'cutoff' ? (
          <Alert>{translate('EditionWantedCutoffHelpText')}</Alert>
        ) : null}
        {isPending ? <LoadingIndicator /> : null}
        {error ? (
          <Alert kind={kinds.DANGER}>{translate('ErrorLoadingPage')}</Alert>
        ) : null}
        {data?.records.length ? (
          <Table columns={columns}>
            <TableBody>
              {data.records.map((edition) => (
                <TableRow key={edition.id}>
                  <TableRowCell>
                    <Link to={`/movie/${edition.tmdbId}`}>
                      {edition.title} ({edition.year})
                    </Link>
                  </TableRowCell>
                  <TableRowCell>
                    {edition.name}{' '}
                    {edition.isPrimary ? (
                      <Label kind={kinds.INFO}>
                        {translate('PrimaryEdition')}
                      </Label>
                    ) : null}
                  </TableRowCell>
                  <TableRowCell>
                    {Object.entries(edition.attributes).map(([key, value]) => (
                      <div key={key}>
                        {key}: {value}
                      </div>
                    ))}
                  </TableRowCell>
                  <TableRowCell>
                    {edition.qualityProfileName ?? edition.qualityProfileId}
                  </TableRowCell>
                  <TableRowCell>
                    {edition.effectiveMonitored
                      ? translate('Yes')
                      : translate('No')}
                    {edition.monitored && !edition.movieMonitored
                      ? ` (${translate('MovieMonitoringDisabled')})`
                      : ''}
                  </TableRowCell>
                  <TableRowCell>
                    <WantedEditionFile edition={edition} />
                  </TableRowCell>
                  {edition.lastSearchTime ? (
                    <RelativeDateCell date={edition.lastSearchTime} />
                  ) : (
                    <TableRowCell>-</TableRowCell>
                  )}
                </TableRow>
              ))}
            </TableBody>
          </Table>
        ) : null}
        {data && !data.records.length ? (
          <Alert>{translate('NoResultsFound')}</Alert>
        ) : null}
        <div className={styles.pagination}>
          <Button isDisabled={page <= 1 || isFetching} onPress={onPrevious}>
            {translate('EditionWantedPrevious')}
          </Button>
          <span>
            {translate('EditionWantedPageSummary', {
              page,
              pages,
              count: data?.totalRecords ?? 0,
            })}
          </span>
          <Button
            isDisabled={!data || page >= pages || isFetching}
            onPress={onNext}
          >
            {translate('EditionWantedNext')}
          </Button>
        </div>
      </PageContentBody>
    </PageContent>
  );
}

export default EditionWanted;
